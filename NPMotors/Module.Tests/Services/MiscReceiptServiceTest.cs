using System.ComponentModel.DataAnnotations;
using System.Data;
using NPMotors.Repository.Interface;
using NPMotors.Repository.Models;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.Models.Enums;
using NPMotors.Repository.RequestFeatures;
using NPMotors.Services.Abstractions.ServiceInterfaces;
using NPMotors.Services.Service;

namespace Module.Tests.Services
{
    // Story 16, RecordMiscReceipt. ValidateMiscReceipt and ToResponseDto are private; they are tested only
    // through the public method. The AR, sales invoice and customer balance fakes log every call, so the tests
    // can show that none of them is touched (AC 3).
    public class MiscReceiptServiceTest
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
            public List<FakeDbTransaction> Transactions { get; } = new();

            public void Open() { State = ConnectionState.Open; _log.Add("open"); }
            public void Close() { State = ConnectionState.Closed; }
            public void Dispose() { }

            public IDbTransaction BeginTransaction()
            {
                _log.Add("begin");
                var tx = new FakeDbTransaction(this, _log);
                Transactions.Add(tx);
                return tx;
            }

            public IDbTransaction BeginTransaction(IsolationLevel il) => BeginTransaction();
            public void ChangeDatabase(string databaseName) { }
            public IDbCommand CreateCommand() => throw new NotSupportedException();
        }

        private class FakeRepositoryContext : IRepositoryContext
        {
            private readonly FakeDbConnection _connection;
            public FakeRepositoryContext(FakeDbConnection connection) { _connection = connection; }
            public IDbConnection GetDatabaseConnection() => _connection;
        }

        // Story 15 dependencies of ReceiptService: Story 16 must never call them.
        private class UntouchedArInvoiceRepository : IArInvoiceRepository
        {
            private readonly List<string> _log;
            public UntouchedArInvoiceRepository(List<string> log) { _log = log; }

            public Task<int> CloseArInvoice(long invoiceId, IDbTransaction tx) { _log.Add("ar-close"); return Task.FromResult(0); }
            public Task<ArInvoice?> GetArInvoiceById(long id) { _log.Add("ar-get"); return Task.FromResult<ArInvoice?>(null); }
            public Task<PagedList<ArAgingDto>> GetArAging(ArAgingParameters parameters) { _log.Add("ar-aging"); throw new NotSupportedException(); }
            public Task<int> UpdateArPayment(ArInvoice ar, decimal expectedBalance, IDbTransaction tx) { _log.Add("ar-update"); return Task.FromResult(0); }
            public Task<long> InsertArInvoice(ArInvoice arInvoice, IDbTransaction tx) { _log.Add("ar-insert"); return Task.FromResult(0L); }
        }

        private class UntouchedSalesInvoiceRepository : ISalesInvoiceRepository
        {
            private readonly List<string> _log;
            public UntouchedSalesInvoiceRepository(List<string> log) { _log = log; }

            public Task<SalesInvoice?> GetInvoiceById(long id) { _log.Add("invoice-get"); return Task.FromResult<SalesInvoice?>(null); }
            public Task<int> UpdateInvoicePayment(SalesInvoice invoice, IDbTransaction tx) { _log.Add("invoice-update"); return Task.FromResult(0); }
            public Task<SalesInvoiceResponseDto> AddSalesInvoice(SalesInvoiceCreateDto invoice) { _log.Add("invoice-add"); throw new NotSupportedException(); }
            public Task<SalesInvoiceResponseDto> PostSalesInvoice(SalesInvoiceCreateDto invoice) { _log.Add("invoice-post"); throw new NotSupportedException(); }
            public Task<PagedList<SalesInvoiceResponseDto>> GetAllSalesInvoices(SalesInvoiceParameters parameters) { _log.Add("invoice-list"); throw new NotSupportedException(); }
            public Task<SalesInvoiceResponseDto?> GetSalesInvoiceById(long id) { _log.Add("invoice-get-dto"); throw new NotSupportedException(); }
            public Task<SalesInvoiceResponseDto> CancelSalesInvoice(long id, SalesInvoiceCancelDto reason, IDbTransaction tx) { _log.Add("invoice-cancel"); throw new NotSupportedException(); }
            public Task<int> UpdatePostedInvoice(SalesInvoice invoice, IDbTransaction tx) { _log.Add("invoice-update-posted"); throw new NotSupportedException(); }
        }

        private class FakeBankAccountRepository : IBankAccountRepository
        {
            private readonly List<string> _log;
            public FakeBankAccountRepository(List<string> log) { _log = log; }

            public BankAccountResponseDto? Account { get; set; } = new() { Id = 1, Name = "Operating", Status = Status.Active };
            public List<long> GetByIdCalls { get; } = new();

            public Task<BankAccountResponseDto?> GetBankAccountById(long id)
            {
                _log.Add("get-account");
                GetByIdCalls.Add(id);
                return Task.FromResult(Account);
            }

            public Task<PagedList<BankAccountResponseDto>> GetAllBankAccounts(BankAccountParameters p) => throw new NotSupportedException();
            public Task<BankAccountResponseDto> AddBankAccount(BankAccountCreateDto b) => throw new NotSupportedException();
            public Task<BankAccountResponseDto?> UpdateBankAccount(long id, BankAccountUpdateDto b) => throw new NotSupportedException();
            public Task<bool> DeleteBankAccount(long id) => throw new NotSupportedException();
            public Task<bool> IsBankAccountReferenced(long id) => throw new NotSupportedException();
            public Task<bool> BankExists(long bankId) => throw new NotSupportedException();
            public Task<BankAccount?> GetBankAccountForUpdate(long id, IDbTransaction tx) => throw new NotSupportedException();
            public Task<int> UpdateBookBalance(long id, decimal bookBalance, IDbTransaction tx) => throw new NotSupportedException();
        }

        private class FakeBankReceiptRepository : IBankReceiptRepository
        {
            private readonly List<string> _log;
            private long _nextId = 88;
            public FakeBankReceiptRepository(List<string> log) { _log = log; }

            public Exception? Throw { get; set; }
            public List<(BankReceipt Receipt, IDbTransaction Tx)> InsertCalls { get; } = new();

            public Task<long> InsertBankReceipt(BankReceipt receipt, IDbTransaction tx)
            {
                _log.Add("insert-receipt");
                InsertCalls.Add((receipt, tx));
                if (Throw != null) throw Throw;
                return Task.FromResult(_nextId++);
            }

            public Task<IEnumerable<BankReceipt>> GetReceiptsByArInvoiceId(long arInvoiceId)
            {
                _log.Add("get-receipts");
                throw new NotSupportedException();
            }
        }

        private class FakePostingService : ITransactionPostingService
        {
            private readonly List<string> _log;
            private long _nextId = 555;
            public FakePostingService(List<string> log) { _log = log; }

            public Exception? Throw { get; set; }
            public List<(PostTransactionRequest Request, IDbTransaction? Tx)> Calls { get; } = new();

            public Task<long> PostTransaction(PostTransactionRequest request, IDbTransaction? tx = null)
            {
                _log.Add("post-transaction");
                Calls.Add((request, tx));
                if (Throw != null) throw Throw;
                return Task.FromResult(_nextId++);
            }
        }

        private readonly FakeDbConnection _connection;
        private readonly FakeBankAccountRepository _accounts;
        private readonly FakeBankReceiptRepository _receipts;
        private readonly FakePostingService _posting;
        private readonly FakeCustomerBalanceService _balances;
        private readonly ReceiptService _service;

        public MiscReceiptServiceTest()
        {
            _connection = new FakeDbConnection(_log);
            _accounts = new FakeBankAccountRepository(_log);
            _receipts = new FakeBankReceiptRepository(_log);
            _posting = new FakePostingService(_log);
            _balances = new FakeCustomerBalanceService(_log);
            _service = new ReceiptService(
                new UntouchedArInvoiceRepository(_log), new UntouchedSalesInvoiceRepository(_log),
                _accounts, _receipts, _posting, new FakeRepositoryContext(_connection), _balances);
        }

        private static RecordMiscReceiptRequest Request(
            string payer = "Acme Corp", DateTime? receiptDate = null, decimal amount = 750.50m,
            PaymentMethod method = PaymentMethod.Check, string? reference = "CHK-55",
            long bankAccountId = 1, string description = "Scrap metal sale") => new()
        {
            Payer = payer,
            ReceiptDate = receiptDate ?? new DateTime(2026, 11, 8),
            Amount = amount,
            PaymentMethod = method,
            Reference = reference,
            BankAccountId = bankAccountId,
            Description = description
        };

        // A rejected request must save nothing: no connection, no transaction, no write.
        private void AssertNothingSaved()
        {
            Assert.DoesNotContain("open", _log);
            Assert.DoesNotContain("begin", _log);
            Assert.Empty(_posting.Calls);
            Assert.Empty(_receipts.InsertCalls);
        }

        private void AssertNoArInvoiceOrCustomerWork()
        {
            Assert.DoesNotContain(_log, entry => entry.StartsWith("ar-") || entry.StartsWith("invoice-"));
            Assert.Empty(_balances.Calls);
        }

        // ---- Public method: RecordMiscReceipt ----

        [Fact]
        public async Task RecordMiscReceipt_PassesBankAccountIdToRepository()
        {
            await _service.RecordMiscReceipt(Request(bankAccountId: 42));

            Assert.Equal(new long[] { 42 }, _accounts.GetByIdCalls);
        }

        [Fact]
        public async Task RecordMiscReceipt_Valid_PostsOneIncomingTransaction()
        {
            await _service.RecordMiscReceipt(Request());

            var (posted, tx) = Assert.Single(_posting.Calls);
            Assert.Equal(1, posted.BankAccountId);
            Assert.Equal(Direction.In, posted.Direction);
            Assert.Equal(750.50m, posted.Amount);
            Assert.Equal(new DateTime(2026, 11, 8), posted.Date);
            Assert.Equal(TransactionType.BankReceipt, posted.Type);
            Assert.Equal("BankReceipt", posted.SourceNumber);
            Assert.Equal("Acme Corp", posted.Party);
            Assert.Equal("Scrap metal sale", posted.Description);
            Assert.Equal(TransactionStatus.Posted, posted.Status);
            Assert.Same(_connection.Transactions.Single(), tx);
        }

        [Fact]
        public async Task RecordMiscReceipt_Valid_InsertsOneReceiptWithNoCustomerInvoiceOrArLink()
        {
            await _service.RecordMiscReceipt(Request(bankAccountId: 1));

            var (receipt, tx) = Assert.Single(_receipts.InsertCalls);
            Assert.Null(receipt.CustomerId);
            Assert.Null(receipt.SalesInvoiceId);
            Assert.Null(receipt.ArInvoiceId);
            Assert.Equal(555, receipt.TransactionId);
            Assert.Equal(1, receipt.BankAccountId);
            Assert.Equal("Acme Corp", receipt.Payer);
            Assert.Equal("Scrap metal sale", receipt.Description);
            Assert.Equal(750.50m, receipt.Amount);
            Assert.Equal(PaymentMethod.Check, receipt.PaymentMethod);
            Assert.Equal("CHK-55", receipt.Reference);
            Assert.Equal(new DateTime(2026, 11, 8), receipt.ReceiptDate);
            Assert.Equal("Posted", receipt.Status);
            Assert.Equal("system", receipt.CreatedBy);
            Assert.Same(_connection.Transactions.Single(), tx);
        }

        [Fact]
        public async Task RecordMiscReceipt_TrimsPayerDescriptionAndReference()
        {
            await _service.RecordMiscReceipt(Request(payer: "  Acme Corp ", description: " Scrap metal sale  ", reference: "  CHK-55 "));

            var receipt = Assert.Single(_receipts.InsertCalls).Receipt;
            Assert.Equal("Acme Corp", receipt.Payer);
            Assert.Equal("Scrap metal sale", receipt.Description);
            Assert.Equal("CHK-55", receipt.Reference);
            Assert.Equal("Acme Corp", _posting.Calls.Single().Request.Party);
            Assert.Equal("Scrap metal sale", _posting.Calls.Single().Request.Description);
        }

        [Fact]
        public async Task RecordMiscReceipt_ReferenceIsOptional()
        {
            var result = await _service.RecordMiscReceipt(Request(reference: null));

            Assert.Null(Assert.Single(_receipts.InsertCalls).Receipt.Reference);
            Assert.Null(result.Reference);
        }

        [Fact]
        public async Task RecordMiscReceipt_Valid_RunsInOrderThenCommitsOnce()
        {
            await _service.RecordMiscReceipt(Request());

            Assert.Equal(
                new[] { "get-account", "open", "begin", "post-transaction", "insert-receipt", "commit" },
                _log);
            var tx = _connection.Transactions.Single();
            Assert.Equal(1, tx.Commits);
            Assert.Equal(0, tx.Rollbacks);
        }

        [Fact]
        public async Task RecordMiscReceipt_Valid_DoesNotTouchArInvoiceSalesInvoiceOrCustomerBalances()
        {
            await _service.RecordMiscReceipt(Request());

            AssertNoArInvoiceOrCustomerWork();
        }

        [Fact]
        public async Task RecordMiscReceipt_Valid_ReturnsTheNewReceipt()
        {
            var result = await _service.RecordMiscReceipt(Request());

            Assert.Equal(88, result.Id);
            Assert.Equal($"RCT-{DateTime.UtcNow.Year}-000088", result.ReceiptNumber);
            Assert.Equal(new DateTime(2026, 11, 8), result.ReceiptDate);
            Assert.Equal("Acme Corp", result.Payer);
            Assert.Equal(750.50m, result.Amount);
            Assert.Equal(PaymentMethod.Check, result.PaymentMethod);
            Assert.Equal("CHK-55", result.Reference);
            Assert.Equal("Scrap metal sale", result.Description);
            Assert.Equal("Operating", result.BankAccountName);
            Assert.Equal(555, result.TransactionId);
        }

        [Fact]
        public async Task RecordMiscReceipt_AccountWithoutName_ReturnsEmptyBankAccountName()
        {
            _accounts.Account = new BankAccountResponseDto { Id = 1, Name = null, Status = Status.Active };

            var result = await _service.RecordMiscReceipt(Request());

            Assert.Equal("", result.BankAccountName);
        }

        // ---- AC 6: each receipt is separate ----

        [Fact]
        public async Task RecordMiscReceipt_CalledTwice_InsertsTwoSeparateReceipts()
        {
            var first = await _service.RecordMiscReceipt(Request(payer: "Acme Corp", amount: 100m));
            var second = await _service.RecordMiscReceipt(Request(payer: "Bolt Ltd", amount: 200m));

            Assert.Equal(2, _receipts.InsertCalls.Count);
            Assert.Equal(2, _posting.Calls.Count);
            Assert.NotSame(_receipts.InsertCalls[0].Receipt, _receipts.InsertCalls[1].Receipt);
            Assert.Equal(88, first.Id);
            Assert.Equal(89, second.Id);
            Assert.Equal(555, first.TransactionId);
            Assert.Equal(556, second.TransactionId);
            // The second receipt did not overwrite the first.
            Assert.Equal("Acme Corp", first.Payer);
            Assert.Equal(100m, first.Amount);
            Assert.Equal("Acme Corp", _receipts.InsertCalls[0].Receipt.Payer);
            Assert.Equal(100m, _receipts.InsertCalls[0].Receipt.Amount);
            Assert.Equal(88, _receipts.InsertCalls[0].Receipt.Id);
        }

        [Fact]
        public async Task RecordMiscReceipt_SamePayerAndAmountTwice_StillTwoReceipts()
        {
            await _service.RecordMiscReceipt(Request());
            await _service.RecordMiscReceipt(Request());

            Assert.Equal(2, _receipts.InsertCalls.Count);
            Assert.Equal(2, _connection.Transactions.Count);
            Assert.All(_connection.Transactions, t => Assert.Equal(1, t.Commits));
        }

        // ---- Rollback (AC 4: all saved together or not at all) ----

        [Fact]
        public async Task RecordMiscReceipt_PostingFails_RollsBackAndRethrowsWithoutInsertingReceipt()
        {
            _posting.Throw = new InvalidOperationException("posting failed");

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _service.RecordMiscReceipt(Request()));

            Assert.Equal("posting failed", ex.Message);
            Assert.Empty(_receipts.InsertCalls);
            var tx = _connection.Transactions.Single();
            Assert.Equal(1, tx.Rollbacks);
            Assert.Equal(0, tx.Commits);
        }

        [Fact]
        public async Task RecordMiscReceipt_InsertFails_RollsBackAndNeverCommits()
        {
            _receipts.Throw = new InvalidOperationException("insert failed");

            await Assert.ThrowsAsync<InvalidOperationException>(() => _service.RecordMiscReceipt(Request()));

            var tx = _connection.Transactions.Single();
            Assert.Equal(1, tx.Rollbacks);
            Assert.Equal(0, tx.Commits);
            Assert.Equal("rollback", _log[^1]);
        }

        // ---- ValidateMiscReceipt (AC 1, 2, 5) ----

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public async Task RecordMiscReceipt_PayerMissing_ThrowsValidationAndSavesNothing(string payer)
        {
            var ex = await Assert.ThrowsAsync<ValidationException>(() => _service.RecordMiscReceipt(Request(payer: payer)));

            Assert.Contains("Payer is required", ex.Message);
            AssertNothingSaved();
        }

        [Fact]
        public async Task RecordMiscReceipt_PayerNull_ThrowsValidationAndSavesNothing()
        {
            var request = Request();
            request.Payer = null!;

            var ex = await Assert.ThrowsAsync<ValidationException>(() => _service.RecordMiscReceipt(request));

            Assert.Contains("Payer is required", ex.Message);
            AssertNothingSaved();
        }

        [Fact]
        public async Task RecordMiscReceipt_DateMissing_ThrowsValidationAndSavesNothing()
        {
            var request = Request();
            request.ReceiptDate = default;

            var ex = await Assert.ThrowsAsync<ValidationException>(() => _service.RecordMiscReceipt(request));

            Assert.Contains("Receipt date is required", ex.Message);
            AssertNothingSaved();
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(-0.01)]
        public async Task RecordMiscReceipt_AmountNotPositive_ThrowsValidationAndSavesNothing(double amount)
        {
            var ex = await Assert.ThrowsAsync<ValidationException>(
                () => _service.RecordMiscReceipt(Request(amount: (decimal)amount)));

            Assert.Contains("Amount must be greater than zero", ex.Message);
            AssertNothingSaved();
        }

        [Fact]
        public async Task RecordMiscReceipt_AmountOneCent_IsAccepted()
        {
            var result = await _service.RecordMiscReceipt(Request(amount: 0.01m));

            Assert.Equal(0.01m, result.Amount);
            Assert.Equal(0.01m, Assert.Single(_posting.Calls).Request.Amount);
        }

        [Fact]
        public async Task RecordMiscReceipt_UndefinedPaymentMethod_ThrowsValidationAndSavesNothing()
        {
            var ex = await Assert.ThrowsAsync<ValidationException>(
                () => _service.RecordMiscReceipt(Request(method: (PaymentMethod)99)));

            Assert.Contains("Cash, Card, Transfer or Check", ex.Message);
            AssertNothingSaved();
        }

        [Theory]
        [InlineData(PaymentMethod.Cash)]
        [InlineData(PaymentMethod.Card)]
        [InlineData(PaymentMethod.Transfer)]
        [InlineData(PaymentMethod.Check)]
        public async Task RecordMiscReceipt_EachPaymentMethod_IsAccepted(PaymentMethod method)
        {
            var result = await _service.RecordMiscReceipt(Request(method: method));

            Assert.Equal(method, result.PaymentMethod);
            Assert.Equal(method, Assert.Single(_receipts.InsertCalls).Receipt.PaymentMethod);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-3)]
        public async Task RecordMiscReceipt_BankAccountIdMissing_ThrowsValidationAndSavesNothing(long bankAccountId)
        {
            var ex = await Assert.ThrowsAsync<ValidationException>(
                () => _service.RecordMiscReceipt(Request(bankAccountId: bankAccountId)));

            Assert.Contains("Bank account is required", ex.Message);
            AssertNothingSaved();
        }

        [Fact]
        public async Task RecordMiscReceipt_AccountNotFound_ThrowsValidationAndSavesNothing()
        {
            _accounts.Account = null;

            var ex = await Assert.ThrowsAsync<ValidationException>(() => _service.RecordMiscReceipt(Request()));

            Assert.Contains("existing bank account is required", ex.Message);
            AssertNothingSaved();
        }

        [Fact]
        public async Task RecordMiscReceipt_InactiveAccount_ThrowsValidationAndSavesNothing()
        {
            _accounts.Account = new BankAccountResponseDto { Id = 1, Name = "Closed", Status = Status.Inactive };

            var ex = await Assert.ThrowsAsync<ValidationException>(() => _service.RecordMiscReceipt(Request()));

            Assert.Contains("must be active", ex.Message);
            AssertNothingSaved();
            AssertNoArInvoiceOrCustomerWork();
        }

        [Fact]
        public async Task RecordMiscReceipt_AccountWithoutStatus_ThrowsValidationAndSavesNothing()
        {
            _accounts.Account = new BankAccountResponseDto { Id = 1, Name = "Odd", Status = null };

            var ex = await Assert.ThrowsAsync<ValidationException>(() => _service.RecordMiscReceipt(Request()));

            Assert.Contains("must be active", ex.Message);
            AssertNothingSaved();
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public async Task RecordMiscReceipt_DescriptionMissing_ThrowsValidationAndSavesNothing(string description)
        {
            var ex = await Assert.ThrowsAsync<ValidationException>(
                () => _service.RecordMiscReceipt(Request(description: description)));

            Assert.Contains("Description is required", ex.Message);
            AssertNothingSaved();
        }

        [Fact]
        public async Task RecordMiscReceipt_DescriptionNull_ThrowsValidationAndSavesNothing()
        {
            var request = Request();
            request.Description = null!;

            var ex = await Assert.ThrowsAsync<ValidationException>(() => _service.RecordMiscReceipt(request));

            Assert.Contains("Description is required", ex.Message);
            AssertNothingSaved();
        }

        [Fact]
        public async Task RecordMiscReceipt_ReferenceOf100Characters_IsAccepted()
        {
            var result = await _service.RecordMiscReceipt(Request(reference: new string('R', 100)));

            Assert.Equal(100, result.Reference!.Length);
        }

        [Fact]
        public async Task RecordMiscReceipt_ReferenceOf101Characters_ThrowsValidationAndSavesNothing()
        {
            var ex = await Assert.ThrowsAsync<ValidationException>(
                () => _service.RecordMiscReceipt(Request(reference: new string('R', 101))));

            Assert.Contains("Reference cannot exceed 100 characters", ex.Message);
            AssertNothingSaved();
        }

        [Fact]
        public async Task RecordMiscReceipt_EveryFieldInvalid_ReportsEveryErrorInOneException()
        {
            _accounts.Account = null;
            var request = new RecordMiscReceiptRequest
            {
                Payer = "",
                ReceiptDate = default,
                Amount = 0m,
                PaymentMethod = (PaymentMethod)99,
                Reference = new string('R', 101),
                BankAccountId = 1,
                Description = ""
            };

            var ex = await Assert.ThrowsAsync<ValidationException>(() => _service.RecordMiscReceipt(request));

            Assert.Equal(
                "Payer is required. Receipt date is required. Amount must be greater than zero. " +
                "Payment method must be Cash, Card, Transfer or Check. An existing bank account is required. " +
                "Description is required. Reference cannot exceed 100 characters.",
                ex.Message);
            AssertNothingSaved();
        }
    }
}
