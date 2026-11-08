using System.ComponentModel.DataAnnotations;
using Dapper;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.Repositories;
using NPMotors.Repository.RequestFeatures;
using NPMotors.Services.Service;

namespace Module.Tests.Services
{
    // Transfers through the real repositories and the test database (AC 1-6 end to end).
    public class TransferServiceIntegrationTest : TestBase, IAsyncLifetime
    {
        private readonly string _marker = $"itest-{Guid.NewGuid():N}".Substring(0, 18);
        private long _bankId;
        private long _operatingId;
        private long _payrollId;
        private TransferService _service = null!;

        private class TransferRow
        {
            public long Id { get; set; }
            public long Bank_Account_Id { get; set; }
            public string Type { get; set; } = "";
            public string Direction { get; set; } = "";
            public decimal Amount { get; set; }
            public string Status { get; set; } = "";
            public string Source_Number { get; set; } = "";
            public string? Transfer_Reference { get; set; }
        }

        public async Task InitializeAsync()
        {
            var accounts = new BankAccountRepository(_repositoryContext);
            var transactions = new TransactionRepository(_repositoryContext);
            var posting = new TransactionPostingService(
                transactions, accounts, new BankStatementLineRepository(_repositoryContext), _repositoryContext);
            _service = new TransferService(accounts, transactions, posting, _repositoryContext);

            using var connection = _repositoryContext.GetDatabaseConnection();
            _bankId = await connection.ExecuteScalarAsync<long>(
                "INSERT INTO bank (name) VALUES (@Name) RETURNING id",
                new { Name = $"{_marker} Bank" });
            _operatingId = await InsertAccountAsync("Operating", 500m);
            _payrollId = await InsertAccountAsync("Payroll", 100m);
        }

        public async Task DisposeAsync()
        {
            using var connection = _repositoryContext.GetDatabaseConnection();
            await connection.ExecuteAsync(
                @"DELETE FROM bank_statement_line WHERE bank_account_id IN (SELECT id FROM bank_account WHERE bank_id = @Id)",
                new { Id = _bankId });
            await connection.ExecuteAsync(
                @"DELETE FROM ""transaction"" WHERE bank_account_id IN (SELECT id FROM bank_account WHERE bank_id = @Id)",
                new { Id = _bankId });
            await connection.ExecuteAsync("DELETE FROM bank_account WHERE bank_id = @Id", new { Id = _bankId });
            await connection.ExecuteAsync("DELETE FROM bank WHERE id = @Id", new { Id = _bankId });
        }

        private async Task<long> InsertAccountAsync(string name, decimal balance, string status = "active")
        {
            using var connection = _repositoryContext.GetDatabaseConnection();
            return await connection.ExecuteScalarAsync<long>(
                @"INSERT INTO bank_account (bank_id, name, type, opening_balance, book_balance, status)
                  VALUES (@BankId, @Name, 'Checking', @Balance, @Balance, @Status) RETURNING id",
                new { BankId = _bankId, Name = $"{_marker} {name}", Balance = balance, Status = status });
        }

        private CreateTransferRequest Request(long source, long destination, decimal amount) => new()
        {
            SourceAccountId = source,
            DestinationAccountId = destination,
            Amount = amount,
            Date = new DateTime(2026, 11, 7),
            Description = "itest transfer"
        };

        private async Task<decimal> BalanceAsync(long accountId)
        {
            using var connection = _repositoryContext.GetDatabaseConnection();
            return await connection.ExecuteScalarAsync<decimal>(
                "SELECT book_balance FROM bank_account WHERE id = @Id", new { Id = accountId });
        }

        private async Task<List<TransferRow>> RowsAsync()
        {
            using var connection = _repositoryContext.GetDatabaseConnection();
            return (await connection.QueryAsync<TransferRow>(
                @"SELECT id, bank_account_id, type, direction::text AS direction, amount, status, source_number, transfer_reference
                  FROM ""transaction""
                  WHERE bank_account_id IN (SELECT id FROM bank_account WHERE bank_id = @Id)
                  ORDER BY id",
                new { Id = _bankId })).ToList();
        }

        private async Task<List<StatementLineResponseDto>> LinesAsync(long accountId) =>
            (await new BankStatementLineRepository(_repositoryContext).GetStatementLines(
                new StatementLineParameters { BankAccountId = accountId, PageSize = 100 })).ToList();

        // ---- AC 4: both balances update together ----

        [Fact]
        public async Task CreateTransfer_UpdatesBothBalances()
        {
            await _service.CreateTransfer(Request(_operatingId, _payrollId, 200m));

            Assert.Equal(300m, await BalanceAsync(_operatingId));
            Assert.Equal(300m, await BalanceAsync(_payrollId));
        }

        [Fact]
        public async Task CreateTransfer_TotalCashDoesNotChange()
        {
            await _service.CreateTransfer(Request(_operatingId, _payrollId, 123.45m));

            Assert.Equal(600m, await BalanceAsync(_operatingId) + await BalanceAsync(_payrollId));
        }

        [Fact]
        public async Task CreateTransfer_ReturnsNewBalancesMatchingTheDatabase()
        {
            var result = await _service.CreateTransfer(Request(_operatingId, _payrollId, 200m));

            Assert.Equal(await BalanceAsync(_operatingId), result.Source_Book_Balance);
            Assert.Equal(await BalanceAsync(_payrollId), result.Destination_Book_Balance);
        }

        [Fact]
        public async Task CreateTransfer_FromHigherIdToLowerId_UpdatesBothBalances()
        {
            await _service.CreateTransfer(Request(_payrollId, _operatingId, 40m));

            Assert.Equal(60m, await BalanceAsync(_payrollId));
            Assert.Equal(540m, await BalanceAsync(_operatingId));
        }

        [Fact]
        public async Task CreateTransfer_AmountEqualsBalance_LeavesSourceAtZero()
        {
            await _service.CreateTransfer(Request(_operatingId, _payrollId, 500m));

            Assert.Equal(0m, await BalanceAsync(_operatingId));
            Assert.Equal(600m, await BalanceAsync(_payrollId));
        }

        // ---- AC 3, 5: two Transfer transactions sharing a reference ----

        [Fact]
        public async Task CreateTransfer_SavesOneOutgoingAndOneIncomingTransactionOfTypeTransfer()
        {
            await _service.CreateTransfer(Request(_operatingId, _payrollId, 200m));

            var rows = await RowsAsync();
            Assert.Equal(2, rows.Count);
            var outgoing = Assert.Single(rows, r => r.Direction == "out");
            var incoming = Assert.Single(rows, r => r.Direction == "in");
            Assert.Equal(_operatingId, outgoing.Bank_Account_Id);
            Assert.Equal(_payrollId, incoming.Bank_Account_Id);
            Assert.All(rows, r =>
            {
                Assert.Equal("Transfer", r.Type);
                Assert.Equal(200m, r.Amount);
                Assert.Equal("posted", r.Status);
            });
        }

        [Fact]
        public async Task CreateTransfer_BothTransactionsShareTheTransferReferenceFromTheOutgoingId()
        {
            var result = await _service.CreateTransfer(Request(_operatingId, _payrollId, 200m));

            var rows = await RowsAsync();
            var outgoing = rows.Single(r => r.Direction == "out");
            Assert.Equal($"TRF-2026-{outgoing.Id:D6}", result.Transfer_Reference);
            Assert.All(rows, r =>
            {
                Assert.Equal(result.Transfer_Reference, r.Transfer_Reference);
                Assert.Equal(result.Transfer_Reference, r.Source_Number);
            });
            Assert.Equal(outgoing.Id, result.Out_Transaction_Id);
            Assert.Equal(rows.Single(r => r.Direction == "in").Id, result.In_Transaction_Id);
        }

        [Fact]
        public async Task CreateTransfer_TwoTransfers_GetDifferentReferences()
        {
            var first = await _service.CreateTransfer(Request(_operatingId, _payrollId, 10m));
            var second = await _service.CreateTransfer(Request(_operatingId, _payrollId, 10m));

            Assert.NotEqual(first.Transfer_Reference, second.Transfer_Reference);
            Assert.Equal(4, (await RowsAsync()).Count);
        }

        // ---- AC 6: each side gets its own statement line ----

        [Fact]
        public async Task CreateTransfer_SourceGetsWithdrawalLineWithRunningBalance()
        {
            var result = await _service.CreateTransfer(Request(_operatingId, _payrollId, 200m));

            var line = Assert.Single(await LinesAsync(_operatingId));
            Assert.Equal(result.Out_Transaction_Id, line.Transaction_Id);
            Assert.Equal("out", line.Direction);
            Assert.Equal(200m, line.Withdrawal);
            Assert.Equal(0m, line.Deposit);
            Assert.Equal(300m, line.Running_Balance);
            Assert.Equal("Transfer", line.Type);
        }

        [Fact]
        public async Task CreateTransfer_DestinationGetsDepositLineWithRunningBalance()
        {
            var result = await _service.CreateTransfer(Request(_operatingId, _payrollId, 200m));

            var line = Assert.Single(await LinesAsync(_payrollId));
            Assert.Equal(result.In_Transaction_Id, line.Transaction_Id);
            Assert.Equal("in", line.Direction);
            Assert.Equal(200m, line.Deposit);
            Assert.Equal(0m, line.Withdrawal);
            Assert.Equal(300m, line.Running_Balance);
            Assert.Equal("Transfer", line.Type);
        }

        [Fact]
        public async Task CreateTransfer_SourceLineNamesDestinationAsPartyAndViceVersa()
        {
            await _service.CreateTransfer(Request(_operatingId, _payrollId, 200m));

            Assert.Equal($"{_marker} Payroll", Assert.Single(await LinesAsync(_operatingId)).Party);
            Assert.Equal($"{_marker} Operating", Assert.Single(await LinesAsync(_payrollId)).Party);
        }

        // ---- AC 1, 2, 4: a rejected or failed transfer changes nothing ----

        private async Task AssertNothingChanged()
        {
            Assert.Empty(await RowsAsync());
            Assert.Empty(await LinesAsync(_operatingId));
            Assert.Empty(await LinesAsync(_payrollId));
            Assert.Equal(500m, await BalanceAsync(_operatingId));
            Assert.Equal(100m, await BalanceAsync(_payrollId));
        }

        [Fact]
        public async Task CreateTransfer_AmountAboveBalance_RejectedAndNothingChanges()
        {
            await Assert.ThrowsAsync<ValidationException>(
                () => _service.CreateTransfer(Request(_operatingId, _payrollId, 500.01m)));

            await AssertNothingChanged();
        }

        [Fact]
        public async Task CreateTransfer_AmountZero_RejectedAndNothingChanges()
        {
            await Assert.ThrowsAsync<ValidationException>(
                () => _service.CreateTransfer(Request(_operatingId, _payrollId, 0m)));

            await AssertNothingChanged();
        }

        [Fact]
        public async Task CreateTransfer_SameAccount_RejectedAndNothingChanges()
        {
            await Assert.ThrowsAsync<ValidationException>(
                () => _service.CreateTransfer(Request(_operatingId, _operatingId, 10m)));

            await AssertNothingChanged();
        }

        [Fact]
        public async Task CreateTransfer_InactiveDestination_RejectedAndNothingChanges()
        {
            var inactiveId = await InsertAccountAsync("Dormant", 0m, "inactive");

            await Assert.ThrowsAsync<ValidationException>(
                () => _service.CreateTransfer(Request(_operatingId, inactiveId, 10m)));

            Assert.Empty(await RowsAsync());
            Assert.Equal(500m, await BalanceAsync(_operatingId));
        }

        [Fact]
        public async Task CreateTransfer_InactiveSource_RejectedAndNothingChanges()
        {
            var inactiveId = await InsertAccountAsync("Dormant", 50m, "inactive");

            await Assert.ThrowsAsync<ValidationException>(
                () => _service.CreateTransfer(Request(inactiveId, _payrollId, 10m)));

            Assert.Empty(await RowsAsync());
            Assert.Equal(100m, await BalanceAsync(_payrollId));
        }

        [Fact]
        public async Task CreateTransfer_UnknownDestination_RejectedAndNothingChanges()
        {
            await Assert.ThrowsAsync<ValidationException>(
                () => _service.CreateTransfer(Request(_operatingId, long.MaxValue, 10m)));

            Assert.Empty(await RowsAsync());
            Assert.Equal(500m, await BalanceAsync(_operatingId));
        }

        [Fact]
        public async Task CreateTransfer_OppositeTransfersAtTheSameTime_BothCompleteWithoutDeadlock()
        {
            // A->B and B->A together: lock order (lower id first) prevents a deadlock.
            var results = await Task.WhenAll(
                _service.CreateTransfer(Request(_operatingId, _payrollId, 50m)),
                _service.CreateTransfer(Request(_payrollId, _operatingId, 30m)));

            Assert.Equal(2, results.Length);
            Assert.Equal(480m, await BalanceAsync(_operatingId)); // 500 - 50 + 30
            Assert.Equal(120m, await BalanceAsync(_payrollId));   // 100 + 50 - 30
        }

        [Fact]
        public async Task CreateTransfer_TwoTransfersTogetherExceedingBalance_OnlyOneSucceeds()
        {
            // 300 + 300 > 500: the row lock makes the second transfer see the reduced balance.
            async Task<bool> Attempt()
            {
                try { await _service.CreateTransfer(Request(_operatingId, _payrollId, 300m)); return true; }
                catch (ValidationException) { return false; }
            }

            var results = await Task.WhenAll(Attempt(), Attempt());

            Assert.Equal(1, results.Count(r => r));
            Assert.Equal(200m, await BalanceAsync(_operatingId));
            Assert.Equal(400m, await BalanceAsync(_payrollId));
            Assert.Equal(2, (await RowsAsync()).Count);
        }
    }
}
