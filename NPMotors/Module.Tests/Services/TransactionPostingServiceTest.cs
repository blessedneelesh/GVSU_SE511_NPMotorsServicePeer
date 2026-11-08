using System.ComponentModel.DataAnnotations;
using System.Data;
using NPMotors.Repository.Interface;
using NPMotors.Repository.Models;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.Models.Enums;
using NPMotors.Repository.RequestFeatures;
using NPMotors.Services.Service;

namespace Module.Tests.Services
{
    public class TransactionPostingServiceTest
    {
        // Every fake writes into one shared call log so the order of calls can be checked.
        private readonly List<string> _log = new();

        private class FakeDbTransaction : IDbTransaction
        {
            private readonly List<string> _log;
            public FakeDbTransaction(IDbConnection? connection, List<string> log)
            {
                Connection = connection;
                _log = log;
            }

            public IDbConnection? Connection { get; }
            public IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
            public int Commits { get; private set; }
            public int Rollbacks { get; private set; }
            public bool Disposed { get; private set; }

            public void Commit() { Commits++; _log.Add("commit"); }
            public void Rollback() { Rollbacks++; _log.Add("rollback"); }
            public void Dispose() { Disposed = true; }
        }

        private class FakeDbConnection : IDbConnection
        {
            private readonly List<string> _log;
            public FakeDbConnection(List<string> log) { _log = log; }

            public string ConnectionString { get; set; } = "";
            public int ConnectionTimeout => 0;
            public string Database => "fake";
            public ConnectionState State { get; private set; } = ConnectionState.Closed;
            public int OpenCalls { get; private set; }
            public bool Disposed { get; private set; }
            public FakeDbTransaction? Transaction { get; private set; }

            public void Open() { OpenCalls++; State = ConnectionState.Open; _log.Add("open"); }
            public void Close() { State = ConnectionState.Closed; }
            public void Dispose() { Disposed = true; }

            public IDbTransaction BeginTransaction()
            {
                _log.Add("begin");
                Transaction = new FakeDbTransaction(this, _log);
                return Transaction;
            }

            public IDbTransaction BeginTransaction(IsolationLevel il) => BeginTransaction();
            public void ChangeDatabase(string databaseName) { }
            public IDbCommand CreateCommand() => throw new NotSupportedException();
        }

        private class FakeRepositoryContext : IRepositoryContext
        {
            public FakeRepositoryContext(FakeDbConnection connection) { Connection = connection; }
            public FakeDbConnection Connection { get; }
            public int Requests { get; private set; }

            public IDbConnection GetDatabaseConnection() { Requests++; return Connection; }
        }

        private class FakeBankAccountRepository : IBankAccountRepository
        {
            private readonly List<string> _log;
            public FakeBankAccountRepository(List<string> log) { _log = log; }

            public BankAccount? Account { get; set; }
            public List<(long Id, IDbTransaction Tx)> ForUpdateCalls { get; } = new();
            public List<(long Id, decimal Balance, IDbTransaction Tx)> UpdateCalls { get; } = new();

            public Task<BankAccount?> GetBankAccountForUpdate(long id, IDbTransaction tx)
            {
                _log.Add("lock-account");
                ForUpdateCalls.Add((id, tx));
                return Task.FromResult(Account);
            }

            public Task<int> UpdateBookBalance(long id, decimal bookBalance, IDbTransaction tx)
            {
                _log.Add("update-balance");
                UpdateCalls.Add((id, bookBalance, tx));
                return Task.FromResult(1);
            }

            public Task<PagedList<BankAccountResponseDto>> GetAllBankAccounts(BankAccountParameters p) => throw new NotSupportedException();
            public Task<BankAccountResponseDto?> GetBankAccountById(long id) => throw new NotSupportedException();
            public Task<BankAccountResponseDto> AddBankAccount(BankAccountCreateDto b) => throw new NotSupportedException();
            public Task<BankAccountResponseDto?> UpdateBankAccount(long id, BankAccountUpdateDto b) => throw new NotSupportedException();
            public Task<bool> DeleteBankAccount(long id) => throw new NotSupportedException();
            public Task<bool> IsBankAccountReferenced(long id) => throw new NotSupportedException();
            public Task<bool> BankExists(long bankId) => throw new NotSupportedException();
        }

        private class FakeTransactionRepository : ITransactionRepository
        {
            private readonly List<string> _log;
            public FakeTransactionRepository(List<string> log) { _log = log; }

            public long NextId { get; set; } = 501;
            public List<(Transaction Transaction, IDbTransaction Tx)> InsertCalls { get; } = new();

            public Task<long> InsertTransaction(Transaction transaction, IDbTransaction tx)
            {
                _log.Add("insert");
                InsertCalls.Add((transaction, tx));
                return Task.FromResult(NextId);
            }

            public Task<PagedList<TransactionResponseDto>> GetAllTransactions(TransactionParameters p) => throw new NotSupportedException();
            public Task<TransactionResponseDto?> GetTransactionById(long id) => throw new NotSupportedException();
        }

        private readonly FakeDbConnection _connection;
        private readonly FakeRepositoryContext _context;
        private readonly FakeBankAccountRepository _accounts;
        private readonly FakeTransactionRepository _transactions;
        private readonly TransactionPostingService _service;

        public TransactionPostingServiceTest()
        {
            _connection = new FakeDbConnection(_log);
            _context = new FakeRepositoryContext(_connection);
            _accounts = new FakeBankAccountRepository(_log)
            {
                Account = new BankAccount { Id = 3, Name = "Operating", BookBalance = 100m, Status = Status.Active }
            };
            _transactions = new FakeTransactionRepository(_log);
            _service = new TransactionPostingService(_transactions, _accounts, _context);
        }

        private static PostTransactionRequest Request(
            Direction direction = Direction.In, decimal amount = 40m,
            TransactionStatus status = TransactionStatus.Posted) => new()
        {
            BankAccountId = 3,
            Direction = direction,
            Amount = amount,
            Date = new DateTime(2026, 3, 15),
            Type = TransactionType.BankReceipt,
            SourceNumber = "RCP-1042",
            Party = "Acme",
            Description = "Deposit",
            Status = status
        };

        // ---- AC 1: direction In/Out, amount > 0 (ValidateTransaction) ----

        [Fact]
        public async Task PostTransaction_DirectionIn_Succeeds()
        {
            var id = await _service.PostTransaction(Request(Direction.In));

            Assert.Equal(501, id);
        }

        [Fact]
        public async Task PostTransaction_DirectionOut_Succeeds()
        {
            var id = await _service.PostTransaction(Request(Direction.Out, 10m));

            Assert.Equal(501, id);
        }

        [Fact]
        public async Task PostTransaction_DirectionNotInOrOut_ThrowsValidationException()
        {
            var ex = await Assert.ThrowsAsync<ValidationException>(
                () => _service.PostTransaction(Request((Direction)5)));

            Assert.Contains("Direction", ex.Message);
            Assert.Empty(_transactions.InsertCalls);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(-0.01)]
        public async Task PostTransaction_AmountZeroOrNegative_ThrowsValidationException(double amount)
        {
            var ex = await Assert.ThrowsAsync<ValidationException>(
                () => _service.PostTransaction(Request(amount: (decimal)amount)));

            Assert.Contains("Amount", ex.Message);
            Assert.Empty(_transactions.InsertCalls);
            Assert.Empty(_accounts.UpdateCalls);
        }

        [Fact]
        public async Task PostTransaction_SmallestPositiveAmount_Succeeds()
        {
            await _service.PostTransaction(Request(Direction.In, 0.01m));

            Assert.Equal(100.01m, Assert.Single(_accounts.UpdateCalls).Balance);
        }

        // ---- AC 2: active account, source type and source number ----

        [Fact]
        public async Task PostTransaction_AccountNotFound_ThrowsValidationException()
        {
            _accounts.Account = null;

            var ex = await Assert.ThrowsAsync<ValidationException>(() => _service.PostTransaction(Request()));

            Assert.Contains("does not exist", ex.Message);
            Assert.Empty(_transactions.InsertCalls);
        }

        [Fact]
        public async Task PostTransaction_InactiveAccount_ThrowsValidationException()
        {
            _accounts.Account!.Status = Status.Inactive;

            var ex = await Assert.ThrowsAsync<ValidationException>(() => _service.PostTransaction(Request()));

            Assert.Contains("active", ex.Message);
            Assert.Empty(_transactions.InsertCalls);
            Assert.Empty(_accounts.UpdateCalls);
        }

        [Fact]
        public async Task PostTransaction_InactiveAccount_DraftIsAlsoRejected()
        {
            _accounts.Account!.Status = Status.Inactive;

            await Assert.ThrowsAsync<ValidationException>(
                () => _service.PostTransaction(Request(status: TransactionStatus.Draft)));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public async Task PostTransaction_SourceNumberMissing_ThrowsValidationException(string sourceNumber)
        {
            var request = Request();
            request.SourceNumber = sourceNumber;

            var ex = await Assert.ThrowsAsync<ValidationException>(() => _service.PostTransaction(request));

            Assert.Contains("Source number", ex.Message);
            Assert.Empty(_transactions.InsertCalls);
        }

        [Fact]
        public async Task PostTransaction_SourceTypeNotDefined_ThrowsValidationException()
        {
            var request = Request();
            request.Type = (TransactionType)99;

            var ex = await Assert.ThrowsAsync<ValidationException>(() => _service.PostTransaction(request));

            Assert.Contains("Source type", ex.Message);
            Assert.Empty(_transactions.InsertCalls);
        }

        [Fact]
        public async Task PostTransaction_StatusNotDefined_ThrowsValidationException()
        {
            var request = Request();
            request.Status = (TransactionStatus)7;

            await Assert.ThrowsAsync<ValidationException>(() => _service.PostTransaction(request));
        }

        [Fact]
        public async Task PostTransaction_SeveralRulesBroken_ReportsEveryError()
        {
            var request = Request(amount: 0m);
            request.SourceNumber = "";

            var ex = await Assert.ThrowsAsync<ValidationException>(() => _service.PostTransaction(request));

            Assert.Contains("Amount", ex.Message);
            Assert.Contains("Source number", ex.Message);
        }

        [Fact]
        public async Task PostTransaction_RecordsSourceTypeAndSourceNumber()
        {
            var request = Request();
            request.Type = TransactionType.BankPayment;
            request.SourceNumber = "  PAY-77  ";

            await _service.PostTransaction(request);

            var inserted = Assert.Single(_transactions.InsertCalls).Transaction;
            Assert.Equal(TransactionType.BankPayment, inserted.Type);
            Assert.Equal("PAY-77", inserted.SourceNumber);
        }

        [Fact]
        public async Task PostTransaction_InsertsEveryRequestField()
        {
            var before = DateTime.UtcNow.AddSeconds(-5);

            await _service.PostTransaction(Request(Direction.Out, 25m));

            var inserted = Assert.Single(_transactions.InsertCalls).Transaction;
            Assert.Equal(3, inserted.BankAccountId);
            Assert.Equal(Direction.Out, inserted.Direction);
            Assert.Equal(25m, inserted.Amount);
            Assert.Equal(new DateTime(2026, 3, 15), inserted.Date);
            Assert.Equal("Acme", inserted.Party);
            Assert.Equal("Deposit", inserted.Description);
            Assert.Equal(TransactionStatus.Posted, inserted.Status);
            Assert.False(string.IsNullOrWhiteSpace(inserted.CreatedBy));
            Assert.InRange(inserted.CreatedAt, before, DateTime.UtcNow.AddSeconds(5));
        }

        [Fact]
        public async Task PostTransaction_LeavesNumberForTheDatabaseToAssign()
        {
            await _service.PostTransaction(Request());

            Assert.Equal(string.Empty, Assert.Single(_transactions.InsertCalls).Transaction.Number);
        }

        // ---- AC 3: ApplyToBookBalance ----

        [Fact]
        public async Task PostTransaction_PostedIncoming_IncreasesBookBalance()
        {
            await _service.PostTransaction(Request(Direction.In, 40m));

            var update = Assert.Single(_accounts.UpdateCalls);
            Assert.Equal(3, update.Id);
            Assert.Equal(140m, update.Balance);
        }

        [Fact]
        public async Task PostTransaction_PostedOutgoing_DecreasesBookBalance()
        {
            await _service.PostTransaction(Request(Direction.Out, 30m));

            Assert.Equal(70m, Assert.Single(_accounts.UpdateCalls).Balance);
        }

        [Fact]
        public async Task PostTransaction_PostedIncoming_WithDecimalAmount_AddsExactly()
        {
            _accounts.Account!.BookBalance = 100.10m;

            await _service.PostTransaction(Request(Direction.In, 0.20m));

            Assert.Equal(100.30m, Assert.Single(_accounts.UpdateCalls).Balance);
        }

        [Fact]
        public async Task PostTransaction_PostedIncoming_OnZeroBalance_Works()
        {
            _accounts.Account!.BookBalance = 0m;

            await _service.PostTransaction(Request(Direction.In, 15m));

            Assert.Equal(15m, Assert.Single(_accounts.UpdateCalls).Balance);
        }

        // ---- AC 4: EnsureSufficientBalance ----

        [Fact]
        public async Task PostTransaction_OutgoingAboveBalance_ThrowsAndWritesNothing()
        {
            var ex = await Assert.ThrowsAsync<ValidationException>(
                () => _service.PostTransaction(Request(Direction.Out, 100.01m)));

            Assert.Contains("book balance", ex.Message);
            Assert.Empty(_transactions.InsertCalls);
            Assert.Empty(_accounts.UpdateCalls);
        }

        [Fact]
        public async Task PostTransaction_OutgoingEqualToBalance_IsAllowedAndLeavesZero()
        {
            await _service.PostTransaction(Request(Direction.Out, 100m));

            Assert.Equal(0m, Assert.Single(_accounts.UpdateCalls).Balance);
        }

        [Fact]
        public async Task PostTransaction_OutgoingOneCentBelowBalance_Works()
        {
            await _service.PostTransaction(Request(Direction.Out, 99.99m));

            Assert.Equal(0.01m, Assert.Single(_accounts.UpdateCalls).Balance);
        }

        [Fact]
        public async Task PostTransaction_IncomingAboveBalance_IsNotLimited()
        {
            await _service.PostTransaction(Request(Direction.In, 5000m));

            Assert.Equal(5100m, Assert.Single(_accounts.UpdateCalls).Balance);
        }

        [Fact]
        public async Task PostTransaction_OutgoingOnZeroBalance_Throws()
        {
            _accounts.Account!.BookBalance = 0m;

            await Assert.ThrowsAsync<ValidationException>(
                () => _service.PostTransaction(Request(Direction.Out, 1m)));
        }

        // ---- AC 5: drafts do not affect balances ----

        [Theory]
        [InlineData(Direction.In, 40)]
        [InlineData(Direction.Out, 30)]
        public async Task PostTransaction_Draft_SavesTransactionButDoesNotChangeBalance(Direction direction, int amount)
        {
            var id = await _service.PostTransaction(Request(direction, amount, TransactionStatus.Draft));

            Assert.Equal(501, id);
            var inserted = Assert.Single(_transactions.InsertCalls).Transaction;
            Assert.Equal(TransactionStatus.Draft, inserted.Status);
            Assert.Empty(_accounts.UpdateCalls);
        }

        [Fact]
        public async Task PostTransaction_DraftOutgoingAboveBalance_IsAllowed()
        {
            await _service.PostTransaction(Request(Direction.Out, 500m, TransactionStatus.Draft));

            Assert.Single(_transactions.InsertCalls);
            Assert.Empty(_accounts.UpdateCalls);
        }

        [Fact]
        public async Task PostTransaction_DraftStillValidatesAmount()
        {
            await Assert.ThrowsAsync<ValidationException>(
                () => _service.PostTransaction(Request(amount: 0m, status: TransactionStatus.Draft)));
        }

        // ---- Sequence: order of calls and transaction ownership ----

        [Fact]
        public async Task PostTransaction_Posted_CallsRepositoriesInSequenceOrder()
        {
            await _service.PostTransaction(Request());

            Assert.Equal(
                new[] { "open", "begin", "lock-account", "insert", "update-balance", "commit" },
                _log.ToArray());
        }

        [Fact]
        public async Task PostTransaction_Draft_SkipsBalanceUpdate()
        {
            await _service.PostTransaction(Request(status: TransactionStatus.Draft));

            Assert.Equal(
                new[] { "open", "begin", "lock-account", "insert", "commit" },
                _log.ToArray());
        }

        [Fact]
        public async Task PostTransaction_PassesRequestAccountIdToLock()
        {
            var request = Request();
            request.BankAccountId = 3;

            await _service.PostTransaction(request);

            Assert.Equal(3, Assert.Single(_accounts.ForUpdateCalls).Id);
        }

        [Fact]
        public async Task PostTransaction_WithoutTx_OpensConnectionOnceAndCommits()
        {
            await _service.PostTransaction(Request());

            Assert.Equal(1, _context.Requests);
            Assert.Equal(1, _connection.OpenCalls);
            Assert.Equal(1, _connection.Transaction!.Commits);
            Assert.Equal(0, _connection.Transaction.Rollbacks);
            Assert.True(_connection.Transaction.Disposed);
            Assert.True(_connection.Disposed);
        }

        [Fact]
        public async Task PostTransaction_WithoutTx_PassesOwnTxToEveryRepositoryCall()
        {
            await _service.PostTransaction(Request());

            var tx = _connection.Transaction!;
            Assert.Same(tx, Assert.Single(_accounts.ForUpdateCalls).Tx);
            Assert.Same(tx, Assert.Single(_transactions.InsertCalls).Tx);
            Assert.Same(tx, Assert.Single(_accounts.UpdateCalls).Tx);
        }

        [Fact]
        public async Task PostTransaction_WithoutTx_ConnectionAlreadyOpen_IsNotOpenedAgain()
        {
            _connection.Open();
            _log.Clear();

            await _service.PostTransaction(Request());

            Assert.Equal(1, _connection.OpenCalls);
        }

        [Fact]
        public async Task PostTransaction_WithoutTx_ValidationFails_RollsBackAndNeverCommits()
        {
            await Assert.ThrowsAsync<ValidationException>(
                () => _service.PostTransaction(Request(Direction.Out, 999m)));

            Assert.Equal(1, _connection.Transaction!.Rollbacks);
            Assert.Equal(0, _connection.Transaction.Commits);
            Assert.True(_connection.Transaction.Disposed);
            Assert.True(_connection.Disposed);
        }

        [Fact]
        public async Task PostTransaction_WithTx_UsesCallersTxAndDoesNotOpenConnection()
        {
            var callerConnection = new FakeDbConnection(_log);
            var callerTx = new FakeDbTransaction(callerConnection, _log);

            var id = await _service.PostTransaction(Request(), callerTx);

            Assert.Equal(501, id);
            Assert.Equal(0, _context.Requests);
            Assert.Same(callerTx, Assert.Single(_accounts.ForUpdateCalls).Tx);
            Assert.Same(callerTx, Assert.Single(_transactions.InsertCalls).Tx);
            Assert.Same(callerTx, Assert.Single(_accounts.UpdateCalls).Tx);
        }

        [Fact]
        public async Task PostTransaction_WithTx_NeverCommitsRollsBackOrDisposesCallersTx()
        {
            var callerTx = new FakeDbTransaction(new FakeDbConnection(_log), _log);

            await _service.PostTransaction(Request(), callerTx);

            Assert.Equal(0, callerTx.Commits);
            Assert.Equal(0, callerTx.Rollbacks);
            Assert.False(callerTx.Disposed);
        }

        [Fact]
        public async Task PostTransaction_WithTx_ValidationFails_LeavesRollbackToCaller()
        {
            var callerTx = new FakeDbTransaction(new FakeDbConnection(_log), _log);

            await Assert.ThrowsAsync<ValidationException>(
                () => _service.PostTransaction(Request(Direction.Out, 999m), callerTx));

            Assert.Equal(0, callerTx.Rollbacks);
            Assert.Equal(0, callerTx.Commits);
        }

        [Fact]
        public async Task PostTransaction_ReturnsIdFromInsert()
        {
            _transactions.NextId = 9001;

            var id = await _service.PostTransaction(Request());

            Assert.Equal(9001, id);
        }
    }
}
