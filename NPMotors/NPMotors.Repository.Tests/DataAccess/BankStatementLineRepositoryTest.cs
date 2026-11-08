using Dapper;
using Npgsql;
using NPMotors.Repository.Models;
using NPMotors.Repository.Models.Enums;
using NPMotors.Repository.Repositories;
using NPMotors.Repository.RequestFeatures;

namespace NPMotors.Repository.Tests.DataAccess
{
    public class BankStatementLineRepositoryTest : TestBase, IAsyncLifetime
    {
        private readonly BankStatementLineRepository _repository;
        private readonly TransactionRepository _transactionRepository;
        private readonly string _marker = $"itest-{Guid.NewGuid():N}".Substring(0, 18);
        private long _bankId;

        public BankStatementLineRepositoryTest()
        {
            _repository = new BankStatementLineRepository(_repositoryContext);
            _transactionRepository = new TransactionRepository(_repositoryContext);
        }

        public async Task InitializeAsync()
        {
            using var connection = _repositoryContext.GetDatabaseConnection();
            _bankId = await connection.ExecuteScalarAsync<long>(
                "INSERT INTO bank (name) VALUES (@Name) RETURNING id",
                new { Name = $"{_marker} Bank" });
        }

        public async Task DisposeAsync()
        {
            // Lines first (FK to transaction and account), then transactions, accounts, bank.
            using var connection = _repositoryContext.GetDatabaseConnection();
            await connection.ExecuteAsync(
                "DELETE FROM bank_statement_line WHERE bank_account_id IN (SELECT id FROM bank_account WHERE bank_id = @Id)",
                new { Id = _bankId });
            await connection.ExecuteAsync(
                @"DELETE FROM ""transaction"" WHERE bank_account_id IN (SELECT id FROM bank_account WHERE bank_id = @Id)",
                new { Id = _bankId });
            await connection.ExecuteAsync("DELETE FROM bank_account WHERE bank_id = @Id", new { Id = _bankId });
            await connection.ExecuteAsync("DELETE FROM bank WHERE id = @Id", new { Id = _bankId });
        }

        private async Task<long> InsertAccountAsync(string suffix = "A")
        {
            using var connection = _repositoryContext.GetDatabaseConnection();
            return await connection.ExecuteScalarAsync<long>(
                @"INSERT INTO bank_account (bank_id, name, type, opening_balance, book_balance, status)
                  VALUES (@BankId, @Name, 'Checking', 100, 100, 'active') RETURNING id",
                new { BankId = _bankId, Name = $"{_marker} Account {suffix}" });
        }

        private async Task<long> InsertTransactionAsync(long accountId, Direction direction = Direction.In)
        {
            using var connection = _repositoryContext.GetDatabaseConnection();
            using var tx = connection.BeginTransaction();
            var id = await _transactionRepository.InsertTransaction(new Transaction
            {
                BankAccountId = accountId,
                Type = TransactionType.BankReceipt,
                SourceNumber = "SRC-1",
                Direction = direction,
                Date = new DateTime(2026, 3, 15),
                Amount = 40m,
                Status = TransactionStatus.Posted,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = "itest"
            }, tx);
            tx.Commit();
            return id;
        }

        private async Task<long> InsertLineAsync(
            long accountId, DateTime? date = null, Direction direction = Direction.In, decimal amount = 40m,
            decimal runningBalance = 140m, TransactionType type = TransactionType.BankReceipt, long? transactionId = null)
        {
            using var connection = _repositoryContext.GetDatabaseConnection();
            using var tx = connection.BeginTransaction();
            var id = await _repository.InsertStatementLine(
                NewLine(accountId, date, direction, amount, runningBalance, type, transactionId), tx);
            tx.Commit();
            return id;
        }

        private static BankStatementLine NewLine(
            long accountId, DateTime? date = null, Direction direction = Direction.In, decimal amount = 40m,
            decimal runningBalance = 140m, TransactionType type = TransactionType.BankReceipt, long? transactionId = null) => new()
        {
            BankAccountId = accountId,
            TransactionId = transactionId,
            PostedDate = date ?? new DateTime(2026, 3, 15),
            Description = "test line",
            Party = "Acme",
            Type = type,
            Direction = direction,
            Deposit = direction == Direction.In ? amount : 0m,
            Withdrawal = direction == Direction.Out ? amount : 0m,
            RunningBalance = runningBalance,
            StatementStatus = StatementStatus.Cleared,
            MatchStatus = MatchStatus.Matched,
            Source = StatementSource.Simulated,
            CreatedAt = DateTime.UtcNow
        };

        private Task<PagedList<NPMotors.Repository.Models.Dto.StatementLineResponseDto>> ListAsync(
            long accountId, string? month = null, string? type = null, int page = 1, int size = 50) =>
            _repository.GetStatementLines(new StatementLineParameters
            {
                BankAccountId = accountId,
                Month = month,
                Type = type,
                PageNumber = page,
                PageSize = size
            });

        // ---- GetLastRunningBalance ----

        [Fact]
        public async Task GetLastRunningBalance_NoLines_ReturnsNull()
        {
            var accountId = await InsertAccountAsync();
            using var connection = _repositoryContext.GetDatabaseConnection();
            using var tx = connection.BeginTransaction();

            var balance = await _repository.GetLastRunningBalance(accountId, tx);

            Assert.Null(balance);
        }

        [Fact]
        public async Task GetLastRunningBalance_ReturnsBalanceOfLatestPostedDate()
        {
            var accountId = await InsertAccountAsync();
            await InsertLineAsync(accountId, new DateTime(2026, 3, 20), runningBalance: 300m);
            await InsertLineAsync(accountId, new DateTime(2026, 3, 10), runningBalance: 100m);
            await InsertLineAsync(accountId, new DateTime(2026, 3, 15), runningBalance: 200m);
            using var connection = _repositoryContext.GetDatabaseConnection();
            using var tx = connection.BeginTransaction();

            var balance = await _repository.GetLastRunningBalance(accountId, tx);

            Assert.Equal(300m, balance);
        }

        [Fact]
        public async Task GetLastRunningBalance_SameDate_ReturnsHighestId()
        {
            var accountId = await InsertAccountAsync();
            await InsertLineAsync(accountId, new DateTime(2026, 3, 15), runningBalance: 111m);
            await InsertLineAsync(accountId, new DateTime(2026, 3, 15), runningBalance: 222m);
            using var connection = _repositoryContext.GetDatabaseConnection();
            using var tx = connection.BeginTransaction();

            var balance = await _repository.GetLastRunningBalance(accountId, tx);

            Assert.Equal(222m, balance);
        }

        [Fact]
        public async Task GetLastRunningBalance_OnlyLooksAtTheGivenAccount()
        {
            var accountA = await InsertAccountAsync("A");
            var accountB = await InsertAccountAsync("B");
            await InsertLineAsync(accountA, runningBalance: 500m);
            await InsertLineAsync(accountB, runningBalance: 900m);
            using var connection = _repositoryContext.GetDatabaseConnection();
            using var tx = connection.BeginTransaction();

            Assert.Equal(500m, await _repository.GetLastRunningBalance(accountA, tx));
            Assert.Equal(900m, await _repository.GetLastRunningBalance(accountB, tx));
        }

        [Fact]
        public async Task GetLastRunningBalance_SeesLineInsertedInTheSameUncommittedTx()
        {
            var accountId = await InsertAccountAsync();
            using var connection = _repositoryContext.GetDatabaseConnection();
            using var tx = connection.BeginTransaction();
            await _repository.InsertStatementLine(NewLine(accountId, runningBalance: 321m), tx);

            var balance = await _repository.GetLastRunningBalance(accountId, tx);

            Assert.Equal(321m, balance);
        }

        // ---- InsertStatementLine ----

        [Fact]
        public async Task InsertStatementLine_StoresEveryColumn()
        {
            var accountId = await InsertAccountAsync();
            var transactionId = await InsertTransactionAsync(accountId, Direction.Out);

            var id = await InsertLineAsync(accountId, new DateTime(2026, 4, 2), Direction.Out, 12.34m, 87.66m,
                TransactionType.BankPayment, transactionId);
            var found = Assert.Single((await ListAsync(accountId)));

            Assert.Equal(id, found.Id);
            Assert.Equal(accountId, found.Bank_Account_Id);
            Assert.Equal(transactionId, found.Transaction_Id);
            Assert.Equal(new DateTime(2026, 4, 2), found.Posted_Date);
            Assert.Null(found.Bank_Reference);
            Assert.Equal("test line", found.Description);
            Assert.Equal("Acme", found.Party);
            Assert.Equal("BankPayment", found.Type);
            Assert.Equal("out", found.Direction);
            Assert.Equal(0m, found.Deposit);
            Assert.Equal(12.34m, found.Withdrawal);
            Assert.Equal(87.66m, found.Running_Balance);
            Assert.Equal("Cleared", found.Statement_Status);
            Assert.Equal("Matched", found.Match_Status);
            Assert.Equal("Simulated", found.Source);
        }

        [Fact]
        public async Task InsertStatementLine_Incoming_StoresDepositAndZeroWithdrawal()
        {
            var accountId = await InsertAccountAsync();

            await InsertLineAsync(accountId, direction: Direction.In, amount: 40m);
            var found = Assert.Single(await ListAsync(accountId));

            Assert.Equal("in", found.Direction);
            Assert.Equal(40m, found.Deposit);
            Assert.Equal(0m, found.Withdrawal);
        }

        [Fact]
        public async Task InsertStatementLine_ReturnsDifferentIdsForTwoLines()
        {
            var accountId = await InsertAccountAsync();

            var first = await InsertLineAsync(accountId);
            var second = await InsertLineAsync(accountId);

            Assert.NotEqual(first, second);
        }

        [Fact]
        public async Task InsertStatementLine_SecondLineForSameTransaction_IsRejected()
        {
            var accountId = await InsertAccountAsync();
            var transactionId = await InsertTransactionAsync(accountId);
            await InsertLineAsync(accountId, transactionId: transactionId);

            await Assert.ThrowsAsync<PostgresException>(
                () => InsertLineAsync(accountId, transactionId: transactionId));

            Assert.Single(await ListAsync(accountId));
        }

        [Fact]
        public async Task InsertStatementLine_BothDepositAndWithdrawal_IsRejectedByTheDatabase()
        {
            var accountId = await InsertAccountAsync();
            var line = NewLine(accountId);
            line.Withdrawal = 5m;
            using var connection = _repositoryContext.GetDatabaseConnection();
            using var tx = connection.BeginTransaction();

            await Assert.ThrowsAsync<PostgresException>(() => _repository.InsertStatementLine(line, tx));
        }

        [Fact]
        public async Task InsertStatementLine_TxRolledBack_LeavesNoLine()
        {
            var accountId = await InsertAccountAsync();
            using (var connection = _repositoryContext.GetDatabaseConnection())
            using (var tx = connection.BeginTransaction())
            {
                await _repository.InsertStatementLine(NewLine(accountId), tx);
                tx.Rollback();
            }

            Assert.Empty(await ListAsync(accountId));
        }

        // ---- GetStatementLines: AC 6 filters ----

        [Fact]
        public async Task GetStatementLines_AccountFilter_ReturnsOnlyThatAccount()
        {
            var accountA = await InsertAccountAsync("A");
            var accountB = await InsertAccountAsync("B");
            var lineA = await InsertLineAsync(accountA);
            await InsertLineAsync(accountB);

            var result = await ListAsync(accountA);

            Assert.Equal(lineA, Assert.Single(result).Id);
            Assert.Equal(1, result.MetaData.TotalCount);
        }

        [Fact]
        public async Task GetStatementLines_NoAccountFilter_IncludesLinesOfEveryAccount()
        {
            var accountA = await InsertAccountAsync("A");
            var accountB = await InsertAccountAsync("B");
            var lineA = await InsertLineAsync(accountA);
            var lineB = await InsertLineAsync(accountB);

            var result = await _repository.GetStatementLines(new StatementLineParameters { PageSize = 100 });

            Assert.Contains(result, l => l.Id == lineA);
            Assert.Contains(result, l => l.Id == lineB);
        }

        [Fact]
        public async Task GetStatementLines_MonthFilter_ReturnsOnlyLinesInThatMonth()
        {
            var accountId = await InsertAccountAsync();
            await InsertLineAsync(accountId, new DateTime(2026, 2, 28));
            var first = await InsertLineAsync(accountId, new DateTime(2026, 3, 1));
            var middle = await InsertLineAsync(accountId, new DateTime(2026, 3, 15));
            var last = await InsertLineAsync(accountId, new DateTime(2026, 3, 31));
            await InsertLineAsync(accountId, new DateTime(2026, 4, 1));

            var result = await ListAsync(accountId, month: "2026-03");

            Assert.Equal(new[] { first, middle, last }, result.Select(l => l.Id).ToArray());
            Assert.Equal(3, result.MetaData.TotalCount);
        }

        [Fact]
        public async Task GetStatementLines_MonthDecember_DoesNotIncludeJanuaryOfNextYear()
        {
            var accountId = await InsertAccountAsync();
            var december = await InsertLineAsync(accountId, new DateTime(2026, 12, 31));
            await InsertLineAsync(accountId, new DateTime(2027, 1, 1));
            await InsertLineAsync(accountId, new DateTime(2026, 11, 30));

            var result = await ListAsync(accountId, month: "2026-12");

            Assert.Equal(december, Assert.Single(result).Id);
        }

        [Fact]
        public async Task GetStatementLines_MonthSameMonthOtherYear_IsExcluded()
        {
            var accountId = await InsertAccountAsync();
            await InsertLineAsync(accountId, new DateTime(2025, 3, 15));
            var line = await InsertLineAsync(accountId, new DateTime(2026, 3, 15));

            var result = await ListAsync(accountId, month: "2026-03");

            Assert.Equal(line, Assert.Single(result).Id);
        }

        [Fact]
        public async Task GetStatementLines_TypeFilter_ReturnsOnlyThatType()
        {
            var accountId = await InsertAccountAsync();
            var receipt = await InsertLineAsync(accountId, type: TransactionType.BankReceipt);
            await InsertLineAsync(accountId, type: TransactionType.BankPayment);
            await InsertLineAsync(accountId, type: TransactionType.Payroll);

            var result = await ListAsync(accountId, type: "BankReceipt");

            Assert.Equal(receipt, Assert.Single(result).Id);
        }

        [Fact]
        public async Task GetStatementLines_TypeFilter_IsCaseInsensitive()
        {
            var accountId = await InsertAccountAsync();
            var payment = await InsertLineAsync(accountId, type: TransactionType.BankPayment);
            await InsertLineAsync(accountId, type: TransactionType.BankReceipt);

            var result = await ListAsync(accountId, type: "bankpayment");

            Assert.Equal(payment, Assert.Single(result).Id);
        }

        [Fact]
        public async Task GetStatementLines_AccountMonthAndType_AllApplyTogether()
        {
            var accountA = await InsertAccountAsync("A");
            var accountB = await InsertAccountAsync("B");
            var match = await InsertLineAsync(accountA, new DateTime(2026, 3, 10), type: TransactionType.BankReceipt);
            await InsertLineAsync(accountA, new DateTime(2026, 4, 10), type: TransactionType.BankReceipt);
            await InsertLineAsync(accountA, new DateTime(2026, 3, 10), type: TransactionType.BankPayment);
            await InsertLineAsync(accountB, new DateTime(2026, 3, 10), type: TransactionType.BankReceipt);

            var result = await ListAsync(accountA, "2026-03", "BankReceipt");

            Assert.Equal(match, Assert.Single(result).Id);
        }

        [Theory]
        [InlineData("2026-13")]
        [InlineData("March")]
        [InlineData("2026-3-15")]
        [InlineData("")]
        [InlineData("   ")]
        public async Task GetStatementLines_InvalidMonth_IsIgnored(string month)
        {
            var accountId = await InsertAccountAsync();
            await InsertLineAsync(accountId, new DateTime(2026, 3, 15));
            await InsertLineAsync(accountId, new DateTime(2026, 5, 15));

            var result = await ListAsync(accountId, month: month);

            Assert.Equal(2, result.Count);
        }

        [Theory]
        [InlineData("NotAType")]
        [InlineData("99")]
        [InlineData("")]
        public async Task GetStatementLines_InvalidType_IsIgnored(string type)
        {
            var accountId = await InsertAccountAsync();
            await InsertLineAsync(accountId, type: TransactionType.BankReceipt);
            await InsertLineAsync(accountId, type: TransactionType.BankPayment);

            var result = await ListAsync(accountId, type: type);

            Assert.Equal(2, result.Count);
        }

        // ---- GetStatementLines: order, paging, joins ----

        [Fact]
        public async Task GetStatementLines_OrderedByPostedDateThenId()
        {
            var accountId = await InsertAccountAsync();
            var march20 = await InsertLineAsync(accountId, new DateTime(2026, 3, 20));
            var march10First = await InsertLineAsync(accountId, new DateTime(2026, 3, 10));
            var march10Second = await InsertLineAsync(accountId, new DateTime(2026, 3, 10));
            var march15 = await InsertLineAsync(accountId, new DateTime(2026, 3, 15));

            var result = await ListAsync(accountId);

            Assert.Equal(new[] { march10First, march10Second, march15, march20 }, result.Select(l => l.Id).ToArray());
        }

        [Fact]
        public async Task GetStatementLines_Paging_SecondPageAndTotalCountOfAllMatches()
        {
            var accountId = await InsertAccountAsync();
            var ids = new List<long>();
            for (var day = 1; day <= 5; day++)
                ids.Add(await InsertLineAsync(accountId, new DateTime(2026, 3, day)));

            var result = await ListAsync(accountId, page: 2, size: 2);

            Assert.Equal(new[] { ids[2], ids[3] }, result.Select(l => l.Id).ToArray());
            Assert.Equal(5, result.MetaData.TotalCount);
            Assert.Equal(2, result.MetaData.CurrentPage);
            Assert.Equal(2, result.MetaData.PageSize);
            Assert.Equal(3, result.MetaData.TotalPages);
        }

        [Fact]
        public async Task GetStatementLines_PageBeyondTheEnd_ReturnsEmptyListWithTotalCount()
        {
            var accountId = await InsertAccountAsync();
            await InsertLineAsync(accountId);

            var result = await ListAsync(accountId, page: 5, size: 2);

            Assert.Empty(result);
            Assert.Equal(1, result.MetaData.TotalCount);
        }

        [Fact]
        public async Task GetStatementLines_PageSizeAboveTheCap_IsClamped()
        {
            var accountId = await InsertAccountAsync();
            await InsertLineAsync(accountId);

            var result = await ListAsync(accountId, size: 5000);

            Assert.Equal(100, result.MetaData.PageSize);
        }

        [Fact]
        public async Task GetStatementLines_NoMatches_ReturnsEmptyPagedListNotNull()
        {
            var accountId = await InsertAccountAsync();
            await InsertLineAsync(accountId, new DateTime(2026, 3, 15));

            var result = await ListAsync(accountId, month: "2030-01");

            Assert.NotNull(result);
            Assert.Empty(result);
            Assert.Equal(0, result.MetaData.TotalCount);
        }

        [Fact]
        public async Task GetStatementLines_IncludesBankAccountNameFromTheJoin()
        {
            var accountId = await InsertAccountAsync("Named");
            await InsertLineAsync(accountId);

            var found = Assert.Single(await ListAsync(accountId));

            Assert.Equal($"{_marker} Account Named", found.Bank_Account_Name);
        }
    }
}
