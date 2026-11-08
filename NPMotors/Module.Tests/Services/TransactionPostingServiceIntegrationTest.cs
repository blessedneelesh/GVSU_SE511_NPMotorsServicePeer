using System.ComponentModel.DataAnnotations;
using Dapper;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.Models.Enums;
using NPMotors.Repository.Repositories;
using NPMotors.Repository.RequestFeatures;
using NPMotors.Services.Service;

namespace Module.Tests.Services
{
    // Posting through the real repositories and the test database (AC 3, 4, 5 end to end).
    public class TransactionPostingServiceIntegrationTest : TestBase, IAsyncLifetime
    {
        private readonly string _marker = $"itest-{Guid.NewGuid():N}".Substring(0, 18);
        private long _bankId;
        private long _accountId;
        private TransactionPostingService _service = null!;
        private TransactionRepository _transactions = null!;

        public async Task InitializeAsync()
        {
            _transactions = new TransactionRepository(_repositoryContext);
            _service = new TransactionPostingService(
                _transactions, new BankAccountRepository(_repositoryContext),
                new BankStatementLineRepository(_repositoryContext), _repositoryContext);

            using var connection = _repositoryContext.GetDatabaseConnection();
            _bankId = await connection.ExecuteScalarAsync<long>(
                "INSERT INTO bank (name) VALUES (@Name) RETURNING id",
                new { Name = $"{_marker} Bank" });
            _accountId = await connection.ExecuteScalarAsync<long>(
                @"INSERT INTO bank_account (bank_id, name, type, opening_balance, book_balance, status)
                  VALUES (@BankId, @Name, 'Checking', 100, 100, 'active') RETURNING id",
                new { BankId = _bankId, Name = $"{_marker} Account" });
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

        private PostTransactionRequest Request(Direction direction, decimal amount, TransactionStatus status) => new()
        {
            BankAccountId = _accountId,
            Direction = direction,
            Amount = amount,
            Date = new DateTime(2026, 3, 15),
            Type = TransactionType.BankAdjustment,
            SourceNumber = "ADJ-1",
            Status = status
        };

        private async Task<decimal> BookBalanceAsync()
        {
            using var connection = _repositoryContext.GetDatabaseConnection();
            return await connection.ExecuteScalarAsync<decimal>(
                "SELECT book_balance FROM bank_account WHERE id = @Id", new { Id = _accountId });
        }

        private async Task<int> TransactionCountAsync()
        {
            using var connection = _repositoryContext.GetDatabaseConnection();
            return await connection.ExecuteScalarAsync<int>(
                @"SELECT COUNT(*) FROM ""transaction"" WHERE bank_account_id = @Id", new { Id = _accountId });
        }

        [Fact]
        public async Task PostTransaction_PostedIncoming_SavesRowAndIncreasesBalance()
        {
            var id = await _service.PostTransaction(Request(Direction.In, 40m, TransactionStatus.Posted));

            var saved = await _transactions.GetTransactionById(id);
            Assert.Equal("posted", saved!.Status);
            Assert.Equal("in", saved.Direction);
            Assert.Equal("BankAdjustment", saved.Type);
            Assert.Equal("ADJ-1", saved.Source_Number);
            Assert.Equal($"TXN-{DateTime.UtcNow.Year}-{id:D6}", saved.Number);
            Assert.Equal(140m, await BookBalanceAsync());
        }

        [Fact]
        public async Task PostTransaction_PostedOutgoing_DecreasesBalance()
        {
            await _service.PostTransaction(Request(Direction.Out, 30m, TransactionStatus.Posted));

            Assert.Equal(70m, await BookBalanceAsync());
        }

        [Fact]
        public async Task PostTransaction_OutgoingAboveBalance_ThrowsAndLeavesNoRowAndBalanceUnchanged()
        {
            await Assert.ThrowsAsync<ValidationException>(
                () => _service.PostTransaction(Request(Direction.Out, 100.01m, TransactionStatus.Posted)));

            Assert.Equal(0, await TransactionCountAsync());
            Assert.Equal(100m, await BookBalanceAsync());
        }

        [Fact]
        public async Task PostTransaction_Draft_SavesRowButBalanceUnchanged()
        {
            var id = await _service.PostTransaction(Request(Direction.Out, 500m, TransactionStatus.Draft));

            Assert.Equal("draft", (await _transactions.GetTransactionById(id))!.Status);
            Assert.Equal(100m, await BookBalanceAsync());
        }

        [Fact]
        public async Task PostTransaction_CallersTxRolledBack_UndoesRowAndBalance()
        {
            using var connection = _repositoryContext.GetDatabaseConnection();
            using (var tx = connection.BeginTransaction())
            {
                await _service.PostTransaction(Request(Direction.In, 40m, TransactionStatus.Posted), tx);
                tx.Rollback();
            }

            Assert.Equal(0, await TransactionCountAsync());
            Assert.Equal(100m, await BookBalanceAsync());
        }

        [Fact]
        public async Task PostTransaction_TwoOutgoingPostingsTogetherExceedingBalance_OnlyOneSucceeds()
        {
            // 70 + 70 > 100: the row lock makes the second posting see the reduced balance.
            var results = await Task.WhenAll(
                Attempt(Request(Direction.Out, 70m, TransactionStatus.Posted)),
                Attempt(Request(Direction.Out, 70m, TransactionStatus.Posted)));

            Assert.Equal(1, results.Count(r => r));
            Assert.Equal(30m, await BookBalanceAsync());
            Assert.Equal(1, await TransactionCountAsync());
        }

        // ---- Story 9: simulated statement lines, end to end ----

        private async Task<List<StatementLineResponseDto>> LinesAsync() =>
            (await new BankStatementLineRepository(_repositoryContext).GetStatementLines(
                new StatementLineParameters { BankAccountId = _accountId, PageSize = 100 })).ToList();

        private PostTransactionRequest DatedRequest(Direction direction, decimal amount, DateTime date) => new()
        {
            BankAccountId = _accountId,
            Direction = direction,
            Amount = amount,
            Date = date,
            Type = TransactionType.BankAdjustment,
            SourceNumber = "ADJ-1",
            Status = TransactionStatus.Posted
        };

        [Fact]
        public async Task PostTransaction_PostedIncoming_CreatesOneSimulatedClearedMatchedLineWithDeposit()
        {
            var id = await _service.PostTransaction(Request(Direction.In, 40m, TransactionStatus.Posted));

            var line = Assert.Single(await LinesAsync());
            Assert.Equal(id, line.Transaction_Id);
            Assert.Equal("Simulated", line.Source);
            Assert.Equal("Cleared", line.Statement_Status);
            Assert.Equal("Matched", line.Match_Status);
            Assert.Equal("in", line.Direction);
            Assert.Equal(40m, line.Deposit);
            Assert.Equal(0m, line.Withdrawal);
            Assert.Equal(new DateTime(2026, 3, 15), line.Posted_Date);
            Assert.Equal("BankAdjustment", line.Type);
            Assert.Equal(140m, line.Running_Balance);
        }

        [Fact]
        public async Task PostTransaction_PostedOutgoing_CreatesLineWithWithdrawal()
        {
            await _service.PostTransaction(Request(Direction.Out, 30m, TransactionStatus.Posted));

            var line = Assert.Single(await LinesAsync());
            Assert.Equal("out", line.Direction);
            Assert.Equal(30m, line.Withdrawal);
            Assert.Equal(0m, line.Deposit);
            Assert.Equal(70m, line.Running_Balance);
        }

        [Fact]
        public async Task PostTransaction_Draft_CreatesNoLine()
        {
            await _service.PostTransaction(Request(Direction.In, 40m, TransactionStatus.Draft));

            Assert.Empty(await LinesAsync());
        }

        [Fact]
        public async Task PostTransaction_ThreePostings_RunningBalanceChainsFromOpeningBalance()
        {
            // Opening balance 100: +40 = 140, -30 = 110, +10 = 120.
            await _service.PostTransaction(DatedRequest(Direction.In, 40m, new DateTime(2026, 3, 1)));
            await _service.PostTransaction(DatedRequest(Direction.Out, 30m, new DateTime(2026, 3, 2)));
            await _service.PostTransaction(DatedRequest(Direction.In, 10m, new DateTime(2026, 3, 3)));

            var lines = await LinesAsync();
            Assert.Equal(new[] { 140m, 110m, 120m }, lines.Select(l => l.Running_Balance).ToArray());
            Assert.Equal(await BookBalanceAsync(), lines.Last().Running_Balance);
        }

        [Fact]
        public async Task PostTransaction_TwoPostingsSameDay_RunningBalanceFollowsInsertOrder()
        {
            await _service.PostTransaction(DatedRequest(Direction.In, 40m, new DateTime(2026, 3, 1)));
            await _service.PostTransaction(DatedRequest(Direction.Out, 15m, new DateTime(2026, 3, 1)));

            var lines = await LinesAsync();
            Assert.Equal(new[] { 140m, 125m }, lines.Select(l => l.Running_Balance).ToArray());
        }

        [Fact]
        public async Task PostTransaction_OutgoingAboveBalance_CreatesNoLine()
        {
            await Assert.ThrowsAsync<ValidationException>(
                () => _service.PostTransaction(Request(Direction.Out, 100.01m, TransactionStatus.Posted)));

            Assert.Empty(await LinesAsync());
        }

        [Fact]
        public async Task PostTransaction_CallersTxRolledBack_UndoesTheLineToo()
        {
            using var connection = _repositoryContext.GetDatabaseConnection();
            using (var tx = connection.BeginTransaction())
            {
                await _service.PostTransaction(Request(Direction.In, 40m, TransactionStatus.Posted), tx);
                tx.Rollback();
            }

            Assert.Empty(await LinesAsync());
        }

        [Fact]
        public async Task PostTransaction_CallersTxCommitted_SavesTransactionAndLineTogether()
        {
            long id;
            using (var connection = _repositoryContext.GetDatabaseConnection())
            using (var tx = connection.BeginTransaction())
            {
                id = await _service.PostTransaction(Request(Direction.In, 40m, TransactionStatus.Posted), tx);
                tx.Commit();
            }

            Assert.Equal(id, Assert.Single(await LinesAsync()).Transaction_Id);
            Assert.Equal(1, await TransactionCountAsync());
        }

        [Fact]
        public async Task PostTransaction_TwoConcurrentOutgoingPostings_EachLineSeesTheOtherInItsBalance()
        {
            // 20 + 20 fit in 100: the row lock makes the second posting read the first line's balance.
            await Task.WhenAll(
                _service.PostTransaction(Request(Direction.Out, 20m, TransactionStatus.Posted)),
                _service.PostTransaction(Request(Direction.Out, 20m, TransactionStatus.Posted)));

            var lines = await LinesAsync();
            Assert.Equal(new[] { 80m, 60m }, lines.Select(l => l.Running_Balance).OrderByDescending(b => b).ToArray());
        }

        private async Task<bool> Attempt(PostTransactionRequest request)
        {
            try
            {
                await _service.PostTransaction(request);
                return true;
            }
            catch (ValidationException)
            {
                return false;
            }
        }
    }
}
