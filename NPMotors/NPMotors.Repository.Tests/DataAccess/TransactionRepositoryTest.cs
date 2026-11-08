using Dapper;
using Npgsql;
using NPMotors.Repository.Models;
using NPMotors.Repository.Models.Enums;
using NPMotors.Repository.Repositories;
using NPMotors.Repository.RequestFeatures;

namespace NPMotors.Repository.Tests.DataAccess
{
    public class TransactionRepositoryTest : TestBase, IAsyncLifetime
    {
        private readonly TransactionRepository _repository;
        private readonly BankAccountRepository _accountRepository;
        private readonly string _marker = $"itest-{Guid.NewGuid():N}".Substring(0, 18);
        private long _bankId;

        public TransactionRepositoryTest()
        {
            _repository = new TransactionRepository(_repositoryContext);
            _accountRepository = new BankAccountRepository(_repositoryContext);
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
            // Transactions first (FK to the account), then the accounts, then the bank.
            using var connection = _repositoryContext.GetDatabaseConnection();
            await connection.ExecuteAsync(
                @"DELETE FROM ""transaction"" WHERE bank_account_id IN (SELECT id FROM bank_account WHERE bank_id = @Id)",
                new { Id = _bankId });
            await connection.ExecuteAsync("DELETE FROM bank_account WHERE bank_id = @Id", new { Id = _bankId });
            await connection.ExecuteAsync("DELETE FROM bank WHERE id = @Id", new { Id = _bankId });
        }

        private async Task<long> InsertAccountAsync(string suffix = "a", decimal balance = 100m, string status = "active")
        {
            using var connection = _repositoryContext.GetDatabaseConnection();
            return await connection.ExecuteScalarAsync<long>(
                @"INSERT INTO bank_account (bank_id, name, type, opening_balance, book_balance, status)
                  VALUES (@BankId, @Name, 'Checking', @Balance, @Balance, @Status) RETURNING id",
                new { BankId = _bankId, Name = $"{_marker} Account {suffix}", Balance = balance, Status = status });
        }

        private async Task<long> InsertTransactionAsync(
            long accountId, Direction direction = Direction.In, DateTime? date = null,
            TransactionType type = TransactionType.BankReceipt, TransactionStatus status = TransactionStatus.Posted,
            string sourceNumber = "SRC-1", decimal amount = 25m)
        {
            using var connection = _repositoryContext.GetDatabaseConnection();
            using var tx = connection.BeginTransaction();
            var id = await _repository.InsertTransaction(new Transaction
            {
                BankAccountId = accountId,
                Type = type,
                SourceNumber = sourceNumber,
                Direction = direction,
                Date = date ?? new DateTime(2026, 3, 15),
                Party = "Acme",
                Amount = amount,
                Description = "test",
                Status = status,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = "itest"
            }, tx);
            tx.Commit();
            return id;
        }

        // ---- InsertTransaction ----

        [Fact]
        public async Task InsertTransaction_StoresEveryColumn()
        {
            var accountId = await InsertAccountAsync();

            var id = await InsertTransactionAsync(accountId, Direction.Out, new DateTime(2026, 4, 2),
                TransactionType.BankPayment, TransactionStatus.Draft, "PAY-9", 12.34m);
            var found = await _repository.GetTransactionById(id);

            Assert.NotNull(found);
            Assert.Equal(accountId, found!.Bank_Account_Id);
            Assert.Equal("BankPayment", found.Type);
            Assert.Equal("PAY-9", found.Source_Number);
            Assert.Equal("out", found.Direction);
            Assert.Equal(new DateTime(2026, 4, 2), found.Date);
            Assert.Equal("Acme", found.Party);
            Assert.Equal(12.34m, found.Amount);
            Assert.Equal("test", found.Description);
            Assert.Equal("draft", found.Status);
            Assert.Equal("itest", found.Created_By);
        }

        [Fact]
        public async Task InsertTransaction_NumberIsTxnYearAndPaddedId_SeparateFromSourceNumber()
        {
            var accountId = await InsertAccountAsync();

            var id = await InsertTransactionAsync(accountId, sourceNumber: "RCP-1042");
            var found = await _repository.GetTransactionById(id);

            Assert.Equal($"TXN-{DateTime.UtcNow.Year}-{id:D6}", found!.Number);
            Assert.Equal("RCP-1042", found.Source_Number);
        }

        [Fact]
        public async Task InsertTransaction_TwoTransactions_GetDifferentIdsAndNumbers()
        {
            var accountId = await InsertAccountAsync();

            var first = await InsertTransactionAsync(accountId);
            var second = await InsertTransactionAsync(accountId);
            var a = await _repository.GetTransactionById(first);
            var b = await _repository.GetTransactionById(second);

            Assert.NotEqual(first, second);
            Assert.NotEqual(a!.Number, b!.Number);
        }

        [Fact]
        public async Task InsertTransaction_SameSourceNumberTwice_IsAllowed()
        {
            var accountId = await InsertAccountAsync();

            var first = await InsertTransactionAsync(accountId, Direction.Out, sourceNumber: "TRF-1");
            var second = await InsertTransactionAsync(accountId, Direction.In, sourceNumber: "TRF-1");

            Assert.NotEqual(first, second);
        }

        [Fact]
        public async Task InsertTransaction_RolledBackTransaction_LeavesNoRow()
        {
            var accountId = await InsertAccountAsync();
            long id;
            using (var connection = _repositoryContext.GetDatabaseConnection())
            using (var tx = connection.BeginTransaction())
            {
                id = await _repository.InsertTransaction(new Transaction
                {
                    BankAccountId = accountId,
                    Type = TransactionType.BankReceipt,
                    SourceNumber = "RB-1",
                    Direction = Direction.In,
                    Date = new DateTime(2026, 3, 15),
                    Amount = 5m,
                    Status = TransactionStatus.Posted,
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = "itest"
                }, tx);
                tx.Rollback();
            }

            Assert.Null(await _repository.GetTransactionById(id));
        }

        // ---- GetAllTransactions: filters ----

        [Fact]
        public async Task GetAllTransactions_FilterByAccount_ReturnsOnlyThatAccount()
        {
            var a = await InsertAccountAsync("a");
            var b = await InsertAccountAsync("b");
            var inA = await InsertTransactionAsync(a);
            await InsertTransactionAsync(b);

            var result = await _repository.GetAllTransactions(new TransactionParameters { BankAccountId = a });

            Assert.Equal(inA, Assert.Single(result).Id);
        }

        [Theory]
        [InlineData("in")]
        [InlineData("IN")]
        [InlineData(" In ")]
        public async Task GetAllTransactions_FilterByDirectionIn_ReturnsOnlyIncoming(string direction)
        {
            var accountId = await InsertAccountAsync();
            var incoming = await InsertTransactionAsync(accountId, Direction.In);
            await InsertTransactionAsync(accountId, Direction.Out);

            var result = await _repository.GetAllTransactions(
                new TransactionParameters { BankAccountId = accountId, Direction = direction });

            Assert.Equal(incoming, Assert.Single(result).Id);
        }

        [Fact]
        public async Task GetAllTransactions_FilterByDirectionOut_ReturnsOnlyOutgoing()
        {
            var accountId = await InsertAccountAsync();
            await InsertTransactionAsync(accountId, Direction.In);
            var outgoing = await InsertTransactionAsync(accountId, Direction.Out);

            var result = await _repository.GetAllTransactions(
                new TransactionParameters { BankAccountId = accountId, Direction = "out" });

            Assert.Equal(outgoing, Assert.Single(result).Id);
        }

        [Theory]
        [InlineData("sideways")]
        [InlineData("")]
        public async Task GetAllTransactions_InvalidOrEmptyDirection_IsIgnored(string direction)
        {
            var accountId = await InsertAccountAsync();
            await InsertTransactionAsync(accountId, Direction.In);
            await InsertTransactionAsync(accountId, Direction.Out);

            var result = await _repository.GetAllTransactions(
                new TransactionParameters { BankAccountId = accountId, Direction = direction });

            Assert.Equal(2, result.Count);
        }

        [Fact]
        public async Task GetAllTransactions_DateFrom_IncludesThatDayAndLater()
        {
            var accountId = await InsertAccountAsync();
            await InsertTransactionAsync(accountId, date: new DateTime(2026, 1, 9));
            var onDay = await InsertTransactionAsync(accountId, date: new DateTime(2026, 1, 10));
            var later = await InsertTransactionAsync(accountId, date: new DateTime(2026, 1, 11));

            var result = await _repository.GetAllTransactions(
                new TransactionParameters { BankAccountId = accountId, DateFrom = new DateTime(2026, 1, 10) });

            Assert.Equal(new[] { later, onDay }, result.Select(t => t.Id).ToArray());
        }

        [Fact]
        public async Task GetAllTransactions_DateTo_IncludesThatDayAndEarlier()
        {
            var accountId = await InsertAccountAsync();
            var earlier = await InsertTransactionAsync(accountId, date: new DateTime(2026, 1, 9));
            var onDay = await InsertTransactionAsync(accountId, date: new DateTime(2026, 1, 10));
            await InsertTransactionAsync(accountId, date: new DateTime(2026, 1, 11));

            var result = await _repository.GetAllTransactions(
                new TransactionParameters { BankAccountId = accountId, DateTo = new DateTime(2026, 1, 10) });

            Assert.Equal(new[] { onDay, earlier }, result.Select(t => t.Id).ToArray());
        }

        [Fact]
        public async Task GetAllTransactions_DateFromAndTo_ReturnsRangeInclusive()
        {
            var accountId = await InsertAccountAsync();
            await InsertTransactionAsync(accountId, date: new DateTime(2026, 1, 4));
            var first = await InsertTransactionAsync(accountId, date: new DateTime(2026, 1, 5));
            var last = await InsertTransactionAsync(accountId, date: new DateTime(2026, 1, 7));
            await InsertTransactionAsync(accountId, date: new DateTime(2026, 1, 8));

            var result = await _repository.GetAllTransactions(new TransactionParameters
            {
                BankAccountId = accountId,
                DateFrom = new DateTime(2026, 1, 5),
                DateTo = new DateTime(2026, 1, 7)
            });

            Assert.Equal(new[] { last, first }, result.Select(t => t.Id).ToArray());
        }

        [Fact]
        public async Task GetAllTransactions_DateFilterIgnoresTimeOfDay()
        {
            var accountId = await InsertAccountAsync();
            var id = await InsertTransactionAsync(accountId, date: new DateTime(2026, 1, 10));

            var result = await _repository.GetAllTransactions(new TransactionParameters
            {
                BankAccountId = accountId,
                DateFrom = new DateTime(2026, 1, 10, 15, 30, 0),
                DateTo = new DateTime(2026, 1, 10, 23, 59, 0)
            });

            Assert.Equal(id, Assert.Single(result).Id);
        }

        [Fact]
        public async Task GetAllTransactions_AccountDirectionAndDate_AreCombinedWithAnd()
        {
            var a = await InsertAccountAsync("a");
            var b = await InsertAccountAsync("b");
            var match = await InsertTransactionAsync(a, Direction.Out, new DateTime(2026, 2, 10));
            await InsertTransactionAsync(a, Direction.In, new DateTime(2026, 2, 10));
            await InsertTransactionAsync(a, Direction.Out, new DateTime(2026, 3, 10));
            await InsertTransactionAsync(b, Direction.Out, new DateTime(2026, 2, 10));

            var result = await _repository.GetAllTransactions(new TransactionParameters
            {
                BankAccountId = a,
                Direction = "out",
                DateFrom = new DateTime(2026, 2, 1),
                DateTo = new DateTime(2026, 2, 28)
            });

            Assert.Equal(match, Assert.Single(result).Id);
        }

        [Fact]
        public async Task GetAllTransactions_NoFilters_IncludesTransactionsOfEveryAccount()
        {
            var a = await InsertAccountAsync("a");
            var b = await InsertAccountAsync("b");
            var inA = await InsertTransactionAsync(a, date: new DateTime(2099, 12, 30));
            var inB = await InsertTransactionAsync(b, date: new DateTime(2099, 12, 31));

            var result = await _repository.GetAllTransactions(new TransactionParameters { PageSize = 100 });

            Assert.Contains(result, t => t.Id == inA);
            Assert.Contains(result, t => t.Id == inB);
        }

        [Fact]
        public async Task GetAllTransactions_DraftsAreListedToo()
        {
            var accountId = await InsertAccountAsync();
            await InsertTransactionAsync(accountId, status: TransactionStatus.Draft);
            await InsertTransactionAsync(accountId, status: TransactionStatus.Posted);

            var result = await _repository.GetAllTransactions(new TransactionParameters { BankAccountId = accountId });

            Assert.Equal(new[] { "draft", "posted" }, result.Select(t => t.Status).OrderBy(s => s).ToArray());
        }

        // ---- GetAllTransactions: order, paging, join ----

        [Fact]
        public async Task GetAllTransactions_OrderedByDateDescThenIdDesc()
        {
            var accountId = await InsertAccountAsync();
            var oldest = await InsertTransactionAsync(accountId, date: new DateTime(2026, 1, 1));
            var newestFirstInserted = await InsertTransactionAsync(accountId, date: new DateTime(2026, 1, 3));
            var newestSecondInserted = await InsertTransactionAsync(accountId, date: new DateTime(2026, 1, 3));
            var middle = await InsertTransactionAsync(accountId, date: new DateTime(2026, 1, 2));

            var result = await _repository.GetAllTransactions(new TransactionParameters { BankAccountId = accountId });

            Assert.Equal(
                new[] { newestSecondInserted, newestFirstInserted, middle, oldest },
                result.Select(t => t.Id).ToArray());
        }

        [Fact]
        public async Task GetAllTransactions_SecondPage_ReturnsRightRowsAndTotalCountOfAllMatches()
        {
            var accountId = await InsertAccountAsync();
            var ids = new List<long>();
            for (var day = 1; day <= 5; day++)
                ids.Add(await InsertTransactionAsync(accountId, date: new DateTime(2026, 1, day)));

            var result = await _repository.GetAllTransactions(
                new TransactionParameters { BankAccountId = accountId, PageNumber = 2, PageSize = 2 });

            // Newest first: day5, day4 | day3, day2 | day1
            Assert.Equal(new[] { ids[2], ids[1] }, result.Select(t => t.Id).ToArray());
            Assert.Equal(5, result.MetaData.TotalCount);
            Assert.Equal(2, result.MetaData.CurrentPage);
            Assert.Equal(2, result.MetaData.PageSize);
            Assert.Equal(3, result.MetaData.TotalPages);
        }

        [Fact]
        public async Task GetAllTransactions_PageSizeAboveCap_IsClamped()
        {
            var parameters = new TransactionParameters { PageSize = 500 };

            var result = await _repository.GetAllTransactions(parameters);

            Assert.Equal(100, result.MetaData.PageSize);
            Assert.True(result.Count <= 100);
        }

        [Fact]
        public async Task GetAllTransactions_NoMatches_ReturnsEmptyPagedList()
        {
            var accountId = await InsertAccountAsync();

            var result = await _repository.GetAllTransactions(new TransactionParameters { BankAccountId = accountId });

            Assert.NotNull(result);
            Assert.Empty(result);
            Assert.Equal(0, result.MetaData.TotalCount);
        }

        [Fact]
        public async Task GetAllTransactions_IncludesBankAccountName_FromJoin()
        {
            var accountId = await InsertAccountAsync("named");
            await InsertTransactionAsync(accountId);

            var result = await _repository.GetAllTransactions(new TransactionParameters { BankAccountId = accountId });

            Assert.Equal($"{_marker} Account named", Assert.Single(result).Bank_Account_Name);
        }

        // ---- GetTransactionById ----

        [Fact]
        public async Task GetTransactionById_Existing_ReturnsRowWithBankAccountName()
        {
            var accountId = await InsertAccountAsync("byid");
            var id = await InsertTransactionAsync(accountId);

            var found = await _repository.GetTransactionById(id);

            Assert.Equal(id, found!.Id);
            Assert.Equal($"{_marker} Account byid", found.Bank_Account_Name);
        }

        [Fact]
        public async Task GetTransactionById_Unknown_ReturnsNull()
        {
            Assert.Null(await _repository.GetTransactionById(long.MaxValue));
        }

        // ---- Story 7 repository methods added for posting ----

        [Fact]
        public async Task GetBankAccountForUpdate_Existing_ReturnsAccountWithBalanceAndStatus()
        {
            var accountId = await InsertAccountAsync(balance: 250.50m);
            using var connection = _repositoryContext.GetDatabaseConnection();
            using var tx = connection.BeginTransaction();

            var account = await _accountRepository.GetBankAccountForUpdate(accountId, tx);

            Assert.NotNull(account);
            Assert.Equal(accountId, account!.Id);
            Assert.Equal($"{_marker} Account a", account.Name);
            Assert.Equal(250.50m, account.BookBalance);
            Assert.Equal(250.50m, account.OpeningBalance);
            Assert.Equal(Status.Active, account.Status);
        }

        [Fact]
        public async Task GetBankAccountForUpdate_InactiveAccount_MapsInactiveStatus()
        {
            var accountId = await InsertAccountAsync(status: "inactive");
            using var connection = _repositoryContext.GetDatabaseConnection();
            using var tx = connection.BeginTransaction();

            var account = await _accountRepository.GetBankAccountForUpdate(accountId, tx);

            Assert.Equal(Status.Inactive, account!.Status);
        }

        [Fact]
        public async Task GetBankAccountForUpdate_Unknown_ReturnsNull()
        {
            using var connection = _repositoryContext.GetDatabaseConnection();
            using var tx = connection.BeginTransaction();

            Assert.Null(await _accountRepository.GetBankAccountForUpdate(long.MaxValue, tx));
        }

        [Fact]
        public async Task GetBankAccountForUpdate_LocksRowUntilTransactionEnds()
        {
            var accountId = await InsertAccountAsync();
            using var first = _repositoryContext.GetDatabaseConnection();
            using var firstTx = first.BeginTransaction();
            await _accountRepository.GetBankAccountForUpdate(accountId, firstTx);

            using var second = _repositoryContext.GetDatabaseConnection();
            using var secondTx = second.BeginTransaction();
            var ex = await Assert.ThrowsAsync<PostgresException>(() => second.ExecuteScalarAsync<long>(
                "SELECT id FROM bank_account WHERE id = @Id FOR UPDATE NOWAIT", new { Id = accountId }, secondTx));

            Assert.Equal("55P03", ex.SqlState); // lock_not_available
        }

        [Fact]
        public async Task UpdateBookBalance_Committed_PersistsNewBalance()
        {
            var accountId = await InsertAccountAsync(balance: 100m);
            using (var connection = _repositoryContext.GetDatabaseConnection())
            using (var tx = connection.BeginTransaction())
            {
                var rows = await _accountRepository.UpdateBookBalance(accountId, 175.25m, tx);
                tx.Commit();
                Assert.Equal(1, rows);
            }

            var stored = await _accountRepository.GetBankAccountById(accountId);
            Assert.Equal(175.25m, stored!.BookBalance);
        }

        [Fact]
        public async Task UpdateBookBalance_RolledBack_KeepsOldBalance()
        {
            var accountId = await InsertAccountAsync(balance: 100m);
            using (var connection = _repositoryContext.GetDatabaseConnection())
            using (var tx = connection.BeginTransaction())
            {
                await _accountRepository.UpdateBookBalance(accountId, 1m, tx);
                tx.Rollback();
            }

            var stored = await _accountRepository.GetBankAccountById(accountId);
            Assert.Equal(100m, stored!.BookBalance);
        }

        [Fact]
        public async Task UpdateBookBalance_UnknownAccount_AffectsNoRows()
        {
            using var connection = _repositoryContext.GetDatabaseConnection();
            using var tx = connection.BeginTransaction();

            var rows = await _accountRepository.UpdateBookBalance(long.MaxValue, 5m, tx);

            Assert.Equal(0, rows);
        }
    }
}
