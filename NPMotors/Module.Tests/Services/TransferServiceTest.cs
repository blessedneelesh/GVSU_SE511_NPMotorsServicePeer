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
    // ValidateTransfer and GenerateTransferReference are private; they are tested only through CreateTransfer.
    public class TransferServiceTest
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

            public void Commit() { Commits++; _log.Add("commit"); }
            public void Rollback() { Rollbacks++; _log.Add("rollback"); }
            public void Dispose() { }
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
            public FakeDbTransaction? Transaction { get; private set; }

            public void Open() { OpenCalls++; State = ConnectionState.Open; _log.Add("open"); }
            public void Close() { State = ConnectionState.Closed; }
            public void Dispose() { }

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
            public IDbConnection GetDatabaseConnection() => Connection;
        }

        private class FakeBankAccountRepository : IBankAccountRepository
        {
            private readonly List<string> _log;
            public FakeBankAccountRepository(List<string> log) { _log = log; }

            public Dictionary<long, BankAccount?> Accounts { get; } = new();
            public List<(long Id, IDbTransaction Tx)> ForUpdateCalls { get; } = new();

            public Task<BankAccount?> GetBankAccountForUpdate(long id, IDbTransaction tx)
            {
                _log.Add($"lock-{id}");
                ForUpdateCalls.Add((id, tx));
                return Task.FromResult(Accounts.TryGetValue(id, out var account) ? account : null);
            }

            public Task<int> UpdateBookBalance(long id, decimal bookBalance, IDbTransaction tx) => throw new NotSupportedException();
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

            public long ReservedId { get; set; } = 123;
            public List<IDbTransaction> NextIdCalls { get; } = new();

            public Task<long> GetNextTransactionId(IDbTransaction tx)
            {
                _log.Add("reserve-id");
                NextIdCalls.Add(tx);
                return Task.FromResult(ReservedId);
            }

            public Task<long> InsertTransaction(Transaction transaction, IDbTransaction tx) => throw new NotSupportedException();
            public Task<PagedList<TransactionResponseDto>> GetAllTransactions(TransactionParameters p) => throw new NotSupportedException();
            public Task<TransactionResponseDto?> GetTransactionById(long id) => throw new NotSupportedException();
        }

        private class FakePostingService : NPMotors.Services.Abstractions.ServiceInterfaces.ITransactionPostingService
        {
            private readonly List<string> _log;
            public FakePostingService(List<string> log) { _log = log; }

            public long OutId { get; set; } = 123;
            public long InId { get; set; } = 124;
            public Exception? ThrowOnOut { get; set; }
            public Exception? ThrowOnIn { get; set; }
            public List<(PostTransactionRequest Request, IDbTransaction? Tx)> Calls { get; } = new();

            public Task<long> PostTransaction(PostTransactionRequest request, IDbTransaction? tx = null)
            {
                _log.Add(request.Direction == Direction.Out ? "post-out" : "post-in");
                Calls.Add((request, tx));
                if (request.Direction == Direction.Out)
                {
                    if (ThrowOnOut != null) throw ThrowOnOut;
                    return Task.FromResult(OutId);
                }
                if (ThrowOnIn != null) throw ThrowOnIn;
                return Task.FromResult(InId);
            }
        }

        private readonly FakeDbConnection _connection;
        private readonly FakeBankAccountRepository _accounts;
        private readonly FakeTransactionRepository _transactions;
        private readonly FakePostingService _posting;
        private readonly TransferService _service;

        public TransferServiceTest()
        {
            _connection = new FakeDbConnection(_log);
            _accounts = new FakeBankAccountRepository(_log);
            _transactions = new FakeTransactionRepository(_log);
            _posting = new FakePostingService(_log);
            _service = new TransferService(_accounts, _transactions, _posting, new FakeRepositoryContext(_connection));

            _accounts.Accounts[1] = Account(1, "Operating", 500m);
            _accounts.Accounts[2] = Account(2, "Payroll", 100m);
        }

        private static BankAccount Account(long id, string name, decimal balance, Status status = Status.Active) =>
            new() { Id = id, Name = name, BookBalance = balance, Status = status };

        private static CreateTransferRequest Request(
            long source = 1, long destination = 2, decimal amount = 200m, DateTime? date = null, string? description = "Payroll funding") =>
            new()
            {
                SourceAccountId = source,
                DestinationAccountId = destination,
                Amount = amount,
                Date = date ?? new DateTime(2026, 11, 7),
                Description = description
            };

        private async Task<ValidationException> AssertRejected(CreateTransferRequest request)
        {
            var ex = await Assert.ThrowsAsync<ValidationException>(() => _service.CreateTransfer(request));
            Assert.Empty(_posting.Calls);
            Assert.Empty(_transactions.NextIdCalls);
            Assert.Equal(1, _connection.Transaction!.Rollbacks);
            Assert.Equal(0, _connection.Transaction.Commits);
            return ex;
        }

        // ---- Response ----

        [Fact]
        public async Task CreateTransfer_Valid_ReturnsReferenceIdsAndNewBalances()
        {
            var result = await _service.CreateTransfer(Request(amount: 200m));

            Assert.Equal("TRF-2026-000123", result.Transfer_Reference);
            Assert.Equal(123, result.Out_Transaction_Id);
            Assert.Equal(124, result.In_Transaction_Id);
            Assert.Equal(200m, result.Amount);
            Assert.Equal(new DateTime(2026, 11, 7), result.Date);
            Assert.Equal("Operating", result.Source_Account_Name);
            Assert.Equal(300m, result.Source_Book_Balance);       // 500 - 200
            Assert.Equal("Payroll", result.Destination_Account_Name);
            Assert.Equal(300m, result.Destination_Book_Balance);  // 100 + 200
        }

        [Fact]
        public async Task CreateTransfer_Valid_TotalCashDoesNotChange()
        {
            var result = await _service.CreateTransfer(Request(amount: 125.50m));

            Assert.Equal(374.50m, result.Source_Book_Balance);
            Assert.Equal(225.50m, result.Destination_Book_Balance);
            Assert.Equal(600m, result.Source_Book_Balance + result.Destination_Book_Balance);
        }

        // ---- AC 3, 6: two postings sharing one reference ----

        [Fact]
        public async Task CreateTransfer_Valid_PostsOutgoingSideOnSourceAccount()
        {
            await _service.CreateTransfer(Request(amount: 200m));

            var outgoing = _posting.Calls[0].Request;
            Assert.Equal(1, outgoing.BankAccountId);
            Assert.Equal(Direction.Out, outgoing.Direction);
            Assert.Equal(200m, outgoing.Amount);
            Assert.Equal(new DateTime(2026, 11, 7), outgoing.Date);
            Assert.Equal(TransactionType.Transfer, outgoing.Type);
            Assert.Equal("TRF-2026-000123", outgoing.SourceNumber);
            Assert.Equal("TRF-2026-000123", outgoing.TransferReference);
            Assert.Equal("Payroll", outgoing.Party);
            Assert.Equal("Payroll funding", outgoing.Description);
            Assert.Equal(TransactionStatus.Posted, outgoing.Status);
        }

        [Fact]
        public async Task CreateTransfer_Valid_PostsIncomingSideOnDestinationAccount()
        {
            await _service.CreateTransfer(Request(amount: 200m));

            var incoming = _posting.Calls[1].Request;
            Assert.Equal(2, incoming.BankAccountId);
            Assert.Equal(Direction.In, incoming.Direction);
            Assert.Equal(200m, incoming.Amount);
            Assert.Equal(new DateTime(2026, 11, 7), incoming.Date);
            Assert.Equal(TransactionType.Transfer, incoming.Type);
            Assert.Equal("TRF-2026-000123", incoming.SourceNumber);
            Assert.Equal("TRF-2026-000123", incoming.TransferReference);
            Assert.Equal("Operating", incoming.Party);
            Assert.Equal("Payroll funding", incoming.Description);
            Assert.Equal(TransactionStatus.Posted, incoming.Status);
        }

        [Fact]
        public async Task CreateTransfer_Valid_OutgoingUsesReservedIdAndIncomingGetsDatabaseId()
        {
            await _service.CreateTransfer(Request());

            Assert.Equal(123, _posting.Calls[0].Request.TransactionId);
            Assert.Null(_posting.Calls[1].Request.TransactionId);
        }

        [Fact]
        public async Task CreateTransfer_NoDescription_BothSidesHaveNullDescription()
        {
            await _service.CreateTransfer(Request(description: null));

            Assert.Null(_posting.Calls[0].Request.Description);
            Assert.Null(_posting.Calls[1].Request.Description);
        }

        // ---- GenerateTransferReference (through CreateTransfer) ----

        [Fact]
        public async Task CreateTransfer_ReferenceUsesYearOfRequestDate()
        {
            var result = await _service.CreateTransfer(Request(date: new DateTime(2025, 12, 31)));

            Assert.Equal("TRF-2025-000123", result.Transfer_Reference);
        }

        [Fact]
        public async Task CreateTransfer_ReferencePadsIdToSixDigits()
        {
            _transactions.ReservedId = 7;

            var result = await _service.CreateTransfer(Request());

            Assert.Equal("TRF-2026-000007", result.Transfer_Reference);
        }

        [Fact]
        public async Task CreateTransfer_ReferenceDoesNotTruncateLongIds()
        {
            _transactions.ReservedId = 1234567;

            var result = await _service.CreateTransfer(Request());

            Assert.Equal("TRF-2026-1234567", result.Transfer_Reference);
        }

        [Fact]
        public async Task CreateTransfer_ReferenceUsesOutgoingTransactionIdNotIncomingId()
        {
            _transactions.ReservedId = 40;
            _posting.OutId = 40;
            _posting.InId = 41;

            var result = await _service.CreateTransfer(Request());

            Assert.Equal("TRF-2026-000040", result.Transfer_Reference);
        }

        // ---- AC 4: one database transaction, in the order from the diagram note ----

        [Fact]
        public async Task CreateTransfer_Valid_FollowsDiagramOrder()
        {
            await _service.CreateTransfer(Request(source: 1, destination: 2));

            Assert.Equal(
                new[] { "open", "begin", "lock-1", "lock-2", "reserve-id", "post-out", "post-in", "commit" },
                _log);
        }

        [Fact]
        public async Task CreateTransfer_Valid_BothPostingsAndIdReservationShareTheOneTransaction()
        {
            await _service.CreateTransfer(Request());

            var tx = _connection.Transaction!;
            Assert.Same(tx, _posting.Calls[0].Tx);
            Assert.Same(tx, _posting.Calls[1].Tx);
            Assert.Same(tx, Assert.Single(_transactions.NextIdCalls));
            Assert.All(_accounts.ForUpdateCalls, call => Assert.Same(tx, call.Tx));
        }

        [Fact]
        public async Task CreateTransfer_Valid_CommitsOnceAndNeverRollsBack()
        {
            await _service.CreateTransfer(Request());

            Assert.Equal(1, _connection.Transaction!.Commits);
            Assert.Equal(0, _connection.Transaction.Rollbacks);
            Assert.Equal(1, _connection.OpenCalls);
        }

        [Fact]
        public async Task CreateTransfer_OutgoingPostingFails_RollsBackAndDoesNotPostIncoming()
        {
            _posting.ThrowOnOut = new InvalidOperationException("boom");

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _service.CreateTransfer(Request()));

            Assert.Equal("boom", ex.Message);
            Assert.Single(_posting.Calls);
            Assert.Equal(1, _connection.Transaction!.Rollbacks);
            Assert.Equal(0, _connection.Transaction.Commits);
        }

        [Fact]
        public async Task CreateTransfer_IncomingPostingFails_RollsBackAndDoesNotCommit()
        {
            _posting.ThrowOnIn = new InvalidOperationException("boom");

            await Assert.ThrowsAsync<InvalidOperationException>(() => _service.CreateTransfer(Request()));

            Assert.Equal(2, _posting.Calls.Count);
            Assert.Equal(1, _connection.Transaction!.Rollbacks);
            Assert.Equal(0, _connection.Transaction.Commits);
        }

        // ---- Deadlock-safe locking: lower id first ----

        [Fact]
        public async Task CreateTransfer_SourceHasLowerId_LocksSourceFirst()
        {
            await _service.CreateTransfer(Request(source: 1, destination: 2));

            Assert.Equal(new long[] { 1, 2 }, _accounts.ForUpdateCalls.Select(c => c.Id));
        }

        [Fact]
        public async Task CreateTransfer_SourceHasHigherId_StillLocksLowerIdFirst()
        {
            await _service.CreateTransfer(Request(source: 2, destination: 1, amount: 50m));

            Assert.Equal(new long[] { 1, 2 }, _accounts.ForUpdateCalls.Select(c => c.Id));
        }

        [Fact]
        public async Task CreateTransfer_SourceHasHigherId_MapsSourceAndDestinationCorrectly()
        {
            var result = await _service.CreateTransfer(Request(source: 2, destination: 1, amount: 50m));

            Assert.Equal("Payroll", result.Source_Account_Name);
            Assert.Equal(50m, result.Source_Book_Balance);          // 100 - 50
            Assert.Equal("Operating", result.Destination_Account_Name);
            Assert.Equal(550m, result.Destination_Book_Balance);    // 500 + 50
            Assert.Equal(2, _posting.Calls[0].Request.BankAccountId);
            Assert.Equal(1, _posting.Calls[1].Request.BankAccountId);
        }

        // ---- AC 1: ValidateTransfer, accounts ----

        [Fact]
        public async Task CreateTransfer_BothAccountsActiveAndDifferent_Passes()
        {
            var result = await _service.CreateTransfer(Request());

            Assert.Equal("TRF-2026-000123", result.Transfer_Reference);
        }

        [Fact]
        public async Task CreateTransfer_SourceDoesNotExist_Rejected()
        {
            var ex = await AssertRejected(Request(source: 99));

            Assert.Contains("source account does not exist", ex.Message);
        }

        [Fact]
        public async Task CreateTransfer_DestinationDoesNotExist_Rejected()
        {
            var ex = await AssertRejected(Request(destination: 99));

            Assert.Contains("destination account does not exist", ex.Message);
        }

        [Fact]
        public async Task CreateTransfer_SourceInactive_Rejected()
        {
            _accounts.Accounts[1] = Account(1, "Operating", 500m, Status.Inactive);

            var ex = await AssertRejected(Request());

            Assert.Contains("source account must be active", ex.Message);
        }

        [Fact]
        public async Task CreateTransfer_DestinationInactive_Rejected()
        {
            _accounts.Accounts[2] = Account(2, "Payroll", 100m, Status.Inactive);

            var ex = await AssertRejected(Request());

            Assert.Contains("destination account must be active", ex.Message);
        }

        [Fact]
        public async Task CreateTransfer_SourceEqualsDestination_Rejected()
        {
            var ex = await AssertRejected(Request(source: 1, destination: 1));

            Assert.Contains("must be different", ex.Message);
        }

        [Fact]
        public async Task CreateTransfer_SourceEqualsDestination_LocksTheAccountOnlyOnce()
        {
            await AssertRejected(Request(source: 1, destination: 1));

            Assert.Equal(new long[] { 1 }, _accounts.ForUpdateCalls.Select(c => c.Id));
        }

        // ---- AC 2: ValidateTransfer, amount ----

        [Fact]
        public async Task CreateTransfer_AmountZero_Rejected()
        {
            var ex = await AssertRejected(Request(amount: 0m));

            Assert.Contains("Amount must be greater than zero", ex.Message);
        }

        [Fact]
        public async Task CreateTransfer_AmountNegative_Rejected()
        {
            var ex = await AssertRejected(Request(amount: -5m));

            Assert.Contains("Amount must be greater than zero", ex.Message);
        }

        [Fact]
        public async Task CreateTransfer_SmallestPositiveAmount_Passes()
        {
            var result = await _service.CreateTransfer(Request(amount: 0.01m));

            Assert.Equal(499.99m, result.Source_Book_Balance);
            Assert.Equal(100.01m, result.Destination_Book_Balance);
        }

        [Fact]
        public async Task CreateTransfer_AmountAboveSourceBalance_Rejected()
        {
            var ex = await AssertRejected(Request(amount: 500.01m));

            Assert.Contains("cannot exceed the source account's book balance", ex.Message);
        }

        [Fact]
        public async Task CreateTransfer_AmountEqualsSourceBalance_PassesAndLeavesZero()
        {
            var result = await _service.CreateTransfer(Request(amount: 500m));

            Assert.Equal(0m, result.Source_Book_Balance);
            Assert.Equal(600m, result.Destination_Book_Balance);
        }

        [Fact]
        public async Task CreateTransfer_BalanceCheckUsesSourceNotDestination()
        {
            // Destination (500) could cover 300, the source (100) cannot.
            _accounts.Accounts[1] = Account(1, "Operating", 100m);
            _accounts.Accounts[2] = Account(2, "Payroll", 500m);

            var ex = await AssertRejected(Request(source: 1, destination: 2, amount: 300m));

            Assert.Contains("cannot exceed the source account's book balance", ex.Message);
        }

        [Fact]
        public async Task CreateTransfer_SeveralProblems_ReportsAllOfThem()
        {
            _accounts.Accounts[2] = Account(2, "Payroll", 100m, Status.Inactive);

            var ex = await AssertRejected(Request(amount: 0m));

            Assert.Contains("destination account must be active", ex.Message);
            Assert.Contains("Amount must be greater than zero", ex.Message);
        }

        [Fact]
        public async Task CreateTransfer_ValidationFails_NoIdReservedAndNothingPosted()
        {
            await AssertRejected(Request(amount: 0m));

            Assert.DoesNotContain("reserve-id", _log);
            Assert.DoesNotContain("post-out", _log);
            Assert.DoesNotContain("post-in", _log);
        }
    }
}
