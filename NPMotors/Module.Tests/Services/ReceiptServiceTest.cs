using System.ComponentModel.DataAnnotations;
using System.Data;
using NPMotors.Repository.Exceptions;
using NPMotors.Repository.Interface;
using NPMotors.Repository.Models;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.Models.Enums;
using NPMotors.Repository.RequestFeatures;
using NPMotors.Services.Abstractions.ServiceInterfaces;
using NPMotors.Services.Service;

namespace Module.Tests.Services
{
    // Story 15, RecordArPayment and GetPaymentHistory. ValidatePaymentAmount, ValidateBankAccount, ApplyPayment,
    // DeterminePaymentStatus and ToResponseDto are private; they are tested only through the two public methods.
    // Base data: AR amount 20000, paid 5000, balance 15000 (Partial); sales invoice total 20000, paid 5000.
    public class ReceiptServiceTest
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
            public FakeDbTransaction? Transaction { get; private set; }

            public void Open() { State = ConnectionState.Open; _log.Add("open"); }
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
            private readonly FakeDbConnection _connection;
            public FakeRepositoryContext(FakeDbConnection connection) { _connection = connection; }
            public IDbConnection GetDatabaseConnection() => _connection;
        }

        private class FakeArInvoiceRepository : IArInvoiceRepository
        {
            private readonly List<string> _log;
            public FakeArInvoiceRepository(List<string> log) { _log = log; }

            public ArInvoice? Ar { get; set; }
            public int UpdateRows { get; set; } = 1;
            public List<long> GetByIdCalls { get; } = new();
            // State of the AR at the moment UpdateArPayment was called.
            public List<(decimal Paid, decimal Balance, PaymentStatus Status, decimal ExpectedBalance, IDbTransaction Tx)> UpdateCalls { get; } = new();

            public Task<int> CloseArInvoice(long invoiceId, IDbTransaction tx) => throw new NotSupportedException();

            public Task<ArInvoice?> GetArInvoiceById(long id)
            {
                _log.Add("get-ar");
                GetByIdCalls.Add(id);
                return Task.FromResult(Ar);
            }

            public Task<NPMotors.Repository.RequestFeatures.PagedList<NPMotors.Repository.Models.Dto.ArAgingDto>> GetArAging(NPMotors.Repository.RequestFeatures.ArAgingParameters parameters) => throw new NotSupportedException();

            public Task<int> UpdateArPayment(ArInvoice ar, decimal expectedBalance, IDbTransaction tx)
            {
                _log.Add("update-ar");
                UpdateCalls.Add((ar.Paid, ar.Balance, ar.Status, expectedBalance, tx));
                return Task.FromResult(UpdateRows);
            }

            public Task<long> InsertArInvoice(ArInvoice arInvoice, IDbTransaction tx) => throw new NotSupportedException();
        }

        private class FakeSalesInvoiceRepository : ISalesInvoiceRepository
        {
            private readonly List<string> _log;
            public FakeSalesInvoiceRepository(List<string> log) { _log = log; }

            public SalesInvoice? Invoice { get; set; }
            public int UpdateRows { get; set; } = 1;
            public List<long> GetByIdCalls { get; } = new();
            // State of the invoice at the moment UpdateInvoicePayment was called.
            public List<(decimal Paid, decimal Balance, PaymentStatus? Status, IDbTransaction Tx)> UpdateCalls { get; } = new();

            public Task<SalesInvoice?> GetInvoiceById(long id)
            {
                _log.Add("get-invoice");
                GetByIdCalls.Add(id);
                return Task.FromResult(Invoice);
            }

            public Task<int> UpdateInvoicePayment(SalesInvoice invoice, IDbTransaction tx)
            {
                _log.Add("update-invoice");
                UpdateCalls.Add((invoice.Paid, invoice.Balance, invoice.PaymentStatus, tx));
                return Task.FromResult(UpdateRows);
            }

            public Task<SalesInvoiceResponseDto> AddSalesInvoice(SalesInvoiceCreateDto invoice) => throw new NotSupportedException();
            public Task<SalesInvoiceResponseDto> PostSalesInvoice(SalesInvoiceCreateDto invoice) => throw new NotSupportedException();
            public Task<PagedList<SalesInvoiceResponseDto>> GetAllSalesInvoices(SalesInvoiceParameters parameters) => throw new NotSupportedException();
            public Task<SalesInvoiceResponseDto?> GetSalesInvoiceById(long id) => throw new NotSupportedException();
            public Task<SalesInvoiceResponseDto> CancelSalesInvoice(long id, SalesInvoiceCancelDto reason, System.Data.IDbTransaction tx) => throw new NotSupportedException();
            public Task<int> UpdatePostedInvoice(SalesInvoice invoice, IDbTransaction tx) => throw new NotSupportedException();
        }

        private class FakeBankAccountRepository : IBankAccountRepository
        {
            private readonly List<string> _log;
            public FakeBankAccountRepository(List<string> log) { _log = log; }

            public BankAccountResponseDto? Account { get; set; } = new() { Id = 1, Status = Status.Active };
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
            public FakeBankReceiptRepository(List<string> log) { _log = log; }

            public long ReturnId { get; set; } = 88;
            public Exception? Throw { get; set; }
            public List<BankReceipt> History { get; set; } = new();
            public List<(BankReceipt Receipt, IDbTransaction Tx)> InsertCalls { get; } = new();
            public List<long> HistoryCalls { get; } = new();

            public Task<long> InsertBankReceipt(BankReceipt receipt, IDbTransaction tx)
            {
                _log.Add("insert-receipt");
                InsertCalls.Add((receipt, tx));
                if (Throw != null) throw Throw;
                return Task.FromResult(ReturnId);
            }

            public Task<IEnumerable<BankReceipt>> GetReceiptsByArInvoiceId(long arInvoiceId)
            {
                _log.Add("get-receipts");
                HistoryCalls.Add(arInvoiceId);
                return Task.FromResult<IEnumerable<BankReceipt>>(History);
            }
        }

        private class FakePostingService : ITransactionPostingService
        {
            private readonly List<string> _log;
            public FakePostingService(List<string> log) { _log = log; }

            public long ReturnId { get; set; } = 555;
            public Exception? Throw { get; set; }
            public List<(PostTransactionRequest Request, IDbTransaction? Tx)> Calls { get; } = new();

            public Task<long> PostTransaction(PostTransactionRequest request, IDbTransaction? tx = null)
            {
                _log.Add("post-transaction");
                Calls.Add((request, tx));
                if (Throw != null) throw Throw;
                return Task.FromResult(ReturnId);
            }
        }

        private readonly FakeDbConnection _connection;
        private readonly FakeArInvoiceRepository _arInvoices;
        private readonly FakeSalesInvoiceRepository _invoices;
        private readonly FakeBankAccountRepository _accounts;
        private readonly FakeBankReceiptRepository _receipts;
        private readonly FakePostingService _posting;
        private readonly FakeCustomerBalanceService _balances;
        private readonly ReceiptService _service;

        public ReceiptServiceTest()
        {
            _connection = new FakeDbConnection(_log);
            _arInvoices = new FakeArInvoiceRepository(_log) { Ar = OpenAr() };
            _invoices = new FakeSalesInvoiceRepository(_log) { Invoice = PostedInvoice() };
            _accounts = new FakeBankAccountRepository(_log);
            _receipts = new FakeBankReceiptRepository(_log);
            _posting = new FakePostingService(_log);
            _balances = new FakeCustomerBalanceService(_log);
            _service = new ReceiptService(
                _arInvoices, _invoices, _accounts, _receipts, _posting, new FakeRepositoryContext(_connection), _balances);
        }

        private static ArInvoice OpenAr() => new()
        {
            Id = 7,
            InvoiceId = 42,
            Number = "AR-000007",
            InvoiceNumber = "INV-000042",
            CustomerName = "Ann Buyer",
            DueDate = new DateTime(2026, 12, 8),
            Amount = 20000m,
            Paid = 5000m,
            Balance = 15000m,
            Status = PaymentStatus.Partial
        };

        private static SalesInvoice PostedInvoice() => new()
        {
            Id = 42,
            Number = "INV-000042",
            CustomerId = 3,
            VehicleId = 5,
            Total = 20000m,
            Paid = 5000m,
            Balance = 15000m,
            PaymentStatus = PaymentStatus.Partial,
            Lifecycle = "posted"
        };

        private static RecordArPaymentRequest Request(
            decimal amount = 4000m, PaymentMethod method = PaymentMethod.Transfer,
            string? reference = "WIRE-9", long bankAccountId = 1, DateTime? receiptDate = null) => new()
        {
            ReceiptDate = receiptDate ?? new DateTime(2026, 11, 8),
            Amount = amount,
            PaymentMethod = method,
            Reference = reference,
            BankAccountId = bankAccountId
        };

        // A rejected request must save nothing: no connection, no transaction, no write.
        private void AssertNothingSaved()
        {
            Assert.DoesNotContain("open", _log);
            Assert.DoesNotContain("begin", _log);
            Assert.Empty(_arInvoices.UpdateCalls);
            Assert.Empty(_invoices.UpdateCalls);
            Assert.Empty(_posting.Calls);
            Assert.Empty(_receipts.InsertCalls);
        }

        // ---- Public method: RecordArPayment ----

        [Fact]
        public async Task RecordArPayment_PassesArIdToRepository()
        {
            await _service.RecordArPayment(7, Request());

            Assert.Equal(new long[] { 7 }, _arInvoices.GetByIdCalls);
        }

        [Fact]
        public async Task RecordArPayment_UnknownAr_ReturnsNullAndSavesNothing()
        {
            _arInvoices.Ar = null;

            var result = await _service.RecordArPayment(999, Request());

            Assert.Null(result);
            Assert.Equal(new long[] { 999 }, _arInvoices.GetByIdCalls);
            Assert.Empty(_invoices.GetByIdCalls);
            Assert.Empty(_accounts.GetByIdCalls);
            AssertNothingSaved();
        }

        [Fact]
        public async Task RecordArPayment_LoadsSalesInvoiceByTheArsInvoiceId()
        {
            await _service.RecordArPayment(7, Request());

            // The AR id is 7 but its sales invoice id is 42.
            Assert.Equal(new long[] { 42 }, _invoices.GetByIdCalls);
        }

        [Fact]
        public async Task RecordArPayment_SalesInvoiceMissing_ThrowsConflictAndSavesNothing()
        {
            _invoices.Invoice = null;

            await Assert.ThrowsAsync<ConflictException>(() => _service.RecordArPayment(7, Request()));

            AssertNothingSaved();
        }

        [Fact]
        public async Task RecordArPayment_ReturnsUpdatedArWithPayments()
        {
            _receipts.History = new List<BankReceipt>
            {
                new() { Id = 1, ReceiptNumber = "RCT-2026-000001", Amount = 5000m, PaymentMethod = PaymentMethod.Card, BankAccountName = "Operating", TransactionId = 100 },
                new() { Id = 88, ReceiptNumber = "RCT-2026-000088", Amount = 4000m, PaymentMethod = PaymentMethod.Transfer, BankAccountName = "Operating", TransactionId = 555 }
            };

            var result = await _service.RecordArPayment(7, Request(4000m));

            Assert.NotNull(result);
            Assert.Equal(7, result!.Id);
            Assert.Equal("AR-000007", result.Number);
            Assert.Equal("INV-000042", result.InvoiceNumber);
            Assert.Equal("Ann Buyer", result.CustomerName);
            Assert.Equal(new DateTime(2026, 12, 8), result.DueDate);
            Assert.Equal(20000m, result.Amount);
            Assert.Equal(9000m, result.Paid);
            Assert.Equal(11000m, result.Balance);
            Assert.Equal(PaymentStatus.Partial, result.Status);
            Assert.Equal(new long[] { 7 }, _receipts.HistoryCalls);
            Assert.Equal(2, result.Payments.Count);
        }

        // ---- ValidatePaymentAmount (AC 1, 2) ----

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(-0.01)]
        public async Task RecordArPayment_AmountNotPositive_ThrowsValidationAndSavesNothing(double amount)
        {
            var ex = await Assert.ThrowsAsync<ValidationException>(
                () => _service.RecordArPayment(7, Request((decimal)amount)));

            Assert.Contains("greater than zero", ex.Message);
            AssertNothingSaved();
        }

        [Fact]
        public async Task RecordArPayment_AmountOneCent_IsAccepted()
        {
            var result = await _service.RecordArPayment(7, Request(0.01m));

            Assert.NotNull(result);
            Assert.Equal(5000.01m, result!.Paid);
            Assert.Equal(14999.99m, result.Balance);
        }

        [Fact]
        public async Task RecordArPayment_AmountAboveBalance_ThrowsValidationAndSavesNothing()
        {
            var ex = await Assert.ThrowsAsync<ValidationException>(
                () => _service.RecordArPayment(7, Request(15000.01m)));

            Assert.Contains("cannot exceed the AR balance", ex.Message);
            AssertNothingSaved();
        }

        [Fact]
        public async Task RecordArPayment_AmountEqualToBalance_IsAccepted()
        {
            var result = await _service.RecordArPayment(7, Request(15000m));

            Assert.NotNull(result);
            Assert.Equal(0m, result!.Balance);
        }

        [Fact]
        public async Task RecordArPayment_ArHasNoOpenBalance_ThrowsValidationAndSavesNothing()
        {
            _arInvoices.Ar!.Paid = 20000m;
            _arInvoices.Ar.Balance = 0m;
            _arInvoices.Ar.Status = PaymentStatus.Paid;

            var ex = await Assert.ThrowsAsync<ValidationException>(() => _service.RecordArPayment(7, Request(1m)));

            Assert.Contains("no open balance", ex.Message);
            AssertNothingSaved();
        }

        [Fact]
        public async Task RecordArPayment_UndefinedPaymentMethod_ThrowsValidationAndSavesNothing()
        {
            var ex = await Assert.ThrowsAsync<ValidationException>(
                () => _service.RecordArPayment(7, Request(method: (PaymentMethod)99)));

            Assert.Contains("Cash, Card, Transfer or Check", ex.Message);
            AssertNothingSaved();
        }

        [Theory]
        [InlineData(PaymentMethod.Cash)]
        [InlineData(PaymentMethod.Card)]
        [InlineData(PaymentMethod.Transfer)]
        [InlineData(PaymentMethod.Check)]
        public async Task RecordArPayment_EachPaymentMethod_IsAcceptedAndStoredOnTheReceipt(PaymentMethod method)
        {
            await _service.RecordArPayment(7, Request(method: method));

            Assert.Equal(method, Assert.Single(_receipts.InsertCalls).Receipt.PaymentMethod);
        }

        [Fact]
        public async Task RecordArPayment_ReferenceOver100Characters_ThrowsValidationAndSavesNothing()
        {
            var ex = await Assert.ThrowsAsync<ValidationException>(
                () => _service.RecordArPayment(7, Request(reference: new string('x', 101))));

            Assert.Contains("100", ex.Message);
            AssertNothingSaved();
        }

        [Fact]
        public async Task RecordArPayment_Reference100Characters_IsAccepted()
        {
            await _service.RecordArPayment(7, Request(reference: new string('x', 100)));

            Assert.Equal(new string('x', 100), Assert.Single(_receipts.InsertCalls).Receipt.Reference);
        }

        [Fact]
        public async Task RecordArPayment_AmountInvalid_DoesNotLookUpTheBankAccount()
        {
            await Assert.ThrowsAsync<ValidationException>(() => _service.RecordArPayment(7, Request(0m)));

            Assert.Empty(_accounts.GetByIdCalls);
        }

        [Fact]
        public async Task RecordArPayment_AmountAndMethodBothInvalid_ReportsBothErrors()
        {
            var ex = await Assert.ThrowsAsync<ValidationException>(
                () => _service.RecordArPayment(7, Request(0m, (PaymentMethod)99)));

            Assert.Contains("greater than zero", ex.Message);
            Assert.Contains("Cash, Card, Transfer or Check", ex.Message);
        }

        // ---- ValidateBankAccount (AC 6) ----

        [Fact]
        public async Task RecordArPayment_PassesBankAccountIdToRepository()
        {
            await _service.RecordArPayment(7, Request(bankAccountId: 12));

            Assert.Equal(new long[] { 12 }, _accounts.GetByIdCalls);
        }

        [Fact]
        public async Task RecordArPayment_BankAccountMissing_ThrowsValidationAndSavesNothing()
        {
            _accounts.Account = null;

            var ex = await Assert.ThrowsAsync<ValidationException>(() => _service.RecordArPayment(7, Request()));

            Assert.Contains("bank account is required", ex.Message);
            AssertNothingSaved();
        }

        [Fact]
        public async Task RecordArPayment_BankAccountInactive_ThrowsValidationAndSavesNothing()
        {
            _accounts.Account = new BankAccountResponseDto { Id = 1, Status = Status.Inactive };

            var ex = await Assert.ThrowsAsync<ValidationException>(() => _service.RecordArPayment(7, Request()));

            Assert.Contains("must be active", ex.Message);
            AssertNothingSaved();
        }

        [Fact]
        public async Task RecordArPayment_BankAccountActive_IsAccepted()
        {
            var result = await _service.RecordArPayment(7, Request());

            Assert.NotNull(result);
        }

        // ---- ApplyPayment (AC 4) ----

        [Fact]
        public async Task RecordArPayment_PartialPayment_UpdatesArPaidBalanceAndStatus()
        {
            await _service.RecordArPayment(7, Request(4000m));

            var update = Assert.Single(_arInvoices.UpdateCalls);
            Assert.Equal(9000m, update.Paid);
            Assert.Equal(11000m, update.Balance);
            Assert.Equal(PaymentStatus.Partial, update.Status);
        }

        [Fact]
        public async Task RecordArPayment_PartialPayment_UpdatesSalesInvoicePaidBalanceAndStatus()
        {
            await _service.RecordArPayment(7, Request(4000m));

            var update = Assert.Single(_invoices.UpdateCalls);
            Assert.Equal(9000m, update.Paid);
            Assert.Equal(11000m, update.Balance);
            Assert.Equal(PaymentStatus.Partial, update.Status);
        }

        [Fact]
        public async Task RecordArPayment_ExpectedBalanceIsTheBalanceBeforeThePayment()
        {
            await _service.RecordArPayment(7, Request(4000m));

            Assert.Equal(15000m, Assert.Single(_arInvoices.UpdateCalls).ExpectedBalance);
        }

        [Fact]
        public async Task RecordArPayment_SalesInvoiceBalanceUsesInvoiceTotalNotArAmount()
        {
            // Invoice total 20500 vs AR amount 20000: each side is worked out from its own total.
            _invoices.Invoice!.Total = 20500m;
            _invoices.Invoice.Balance = 15500m;

            await _service.RecordArPayment(7, Request(4000m));

            Assert.Equal(11000m, Assert.Single(_arInvoices.UpdateCalls).Balance);
            Assert.Equal(11500m, Assert.Single(_invoices.UpdateCalls).Balance);
        }

        [Fact]
        public async Task RecordArPayment_SecondPaymentOnSameInvoiceAddsToWhatWasPaid()
        {
            // The sales invoice already carries 5000 paid; the payment is added, not set.
            await _service.RecordArPayment(7, Request(1000m));

            Assert.Equal(6000m, Assert.Single(_invoices.UpdateCalls).Paid);
            Assert.Equal(6000m, Assert.Single(_arInvoices.UpdateCalls).Paid);
        }

        // ---- DeterminePaymentStatus (AC 5) ----

        [Fact]
        public async Task RecordArPayment_FullPayment_SetsArAndInvoicePaidWithZeroBalance()
        {
            var result = await _service.RecordArPayment(7, Request(15000m));

            var arUpdate = Assert.Single(_arInvoices.UpdateCalls);
            Assert.Equal(20000m, arUpdate.Paid);
            Assert.Equal(0m, arUpdate.Balance);
            Assert.Equal(PaymentStatus.Paid, arUpdate.Status);
            var invoiceUpdate = Assert.Single(_invoices.UpdateCalls);
            Assert.Equal(20000m, invoiceUpdate.Paid);
            Assert.Equal(0m, invoiceUpdate.Balance);
            Assert.Equal(PaymentStatus.Paid, invoiceUpdate.Status);
            Assert.Equal(PaymentStatus.Paid, result!.Status);
        }

        [Fact]
        public async Task RecordArPayment_OneCentShortOfFull_SetsPartial()
        {
            var result = await _service.RecordArPayment(7, Request(14999.99m));

            Assert.Equal(PaymentStatus.Partial, Assert.Single(_arInvoices.UpdateCalls).Status);
            Assert.Equal(PaymentStatus.Partial, Assert.Single(_invoices.UpdateCalls).Status);
            Assert.Equal(0.01m, result!.Balance);
        }

        [Fact]
        public async Task RecordArPayment_FirstPaymentOnUnpaidAr_SetsPartial()
        {
            _arInvoices.Ar!.Paid = 0m;
            _arInvoices.Ar.Balance = 20000m;
            _arInvoices.Ar.Status = PaymentStatus.Unpaid;
            _invoices.Invoice!.Paid = 0m;
            _invoices.Invoice.Balance = 20000m;
            _invoices.Invoice.PaymentStatus = PaymentStatus.Unpaid;

            var result = await _service.RecordArPayment(7, Request(2500m));

            Assert.Equal(PaymentStatus.Partial, result!.Status);
            Assert.Equal(PaymentStatus.Partial, Assert.Single(_invoices.UpdateCalls).Status);
            Assert.Equal(17500m, result.Balance);
        }

        [Fact]
        public async Task RecordArPayment_PayingAnOverdueAr_Partial_SetsPartial()
        {
            _arInvoices.Ar!.Status = PaymentStatus.Overdue;

            var result = await _service.RecordArPayment(7, Request(1000m));

            Assert.Equal(PaymentStatus.Partial, result!.Status);
        }

        // ---- Save together (AC 3, 4, 7, 8) ----

        [Fact]
        public async Task RecordArPayment_SavesInOneTransactionInTheDiagramOrder()
        {
            await _service.RecordArPayment(7, Request());

            Assert.Equal(
                new[]
                {
                    "get-ar", "get-invoice", "get-account",
                    "open", "begin", "update-ar", "update-invoice", "post-transaction", "insert-receipt", "update-customer-totals", "commit",
                    "get-receipts"
                },
                _log);
        }

        [Fact]
        public async Task RecordArPayment_EveryWriteUsesTheSameTransaction()
        {
            await _service.RecordArPayment(7, Request());

            var tx = _connection.Transaction!;
            Assert.Same(tx, Assert.Single(_arInvoices.UpdateCalls).Tx);
            Assert.Same(tx, Assert.Single(_invoices.UpdateCalls).Tx);
            Assert.Same(tx, Assert.Single(_posting.Calls).Tx);
            Assert.Same(tx, Assert.Single(_receipts.InsertCalls).Tx);
            Assert.Equal(1, tx.Commits);
            Assert.Equal(0, tx.Rollbacks);
        }

        [Fact]
        public async Task RecordArPayment_PostsABankReceiptTransactionIntoTheSelectedAccount()
        {
            var date = new DateTime(2026, 11, 20);

            await _service.RecordArPayment(7, Request(4000m, bankAccountId: 12, receiptDate: date));

            var request = Assert.Single(_posting.Calls).Request;
            Assert.Equal(12, request.BankAccountId);
            Assert.Equal(Direction.In, request.Direction);
            Assert.Equal(4000m, request.Amount);
            Assert.Equal(date, request.Date);
            Assert.Equal(TransactionType.BankReceipt, request.Type);
            Assert.Equal("INV-000042", request.SourceNumber);
            Assert.Equal(TransactionStatus.Posted, request.Status);
        }

        [Fact]
        public async Task RecordArPayment_InsertsABankReceiptLinkedToTheTransaction()
        {
            _posting.ReturnId = 777;
            var date = new DateTime(2026, 11, 20);

            await _service.RecordArPayment(7, Request(4000m, PaymentMethod.Check, "  CHK-1001  ", 12, date));

            var receipt = Assert.Single(_receipts.InsertCalls).Receipt;
            Assert.Equal(777, receipt.TransactionId);
            Assert.Equal(12, receipt.BankAccountId);
            Assert.Equal(3, receipt.CustomerId);
            Assert.Equal(42, receipt.SalesInvoiceId);
            Assert.Equal(7, receipt.ArInvoiceId);
            Assert.Equal(date, receipt.ReceiptDate);
            Assert.Equal(PaymentMethod.Check, receipt.PaymentMethod);
            Assert.Equal("CHK-1001", receipt.Reference);
            Assert.Equal(4000m, receipt.Amount);
            Assert.Equal("Posted", receipt.Status);
            Assert.Equal("system", receipt.CreatedBy);
        }

        [Fact]
        public async Task RecordArPayment_NoReference_StoresNullReference()
        {
            await _service.RecordArPayment(7, Request(reference: null));

            Assert.Null(Assert.Single(_receipts.InsertCalls).Receipt.Reference);
        }

        [Fact]
        public async Task RecordArPayment_TwoPayments_InsertTwoSeparateReceiptsWithTheirOwnTransactions()
        {
            _posting.ReturnId = 555;
            await _service.RecordArPayment(7, Request(1000m));
            _posting.ReturnId = 556;
            _arInvoices.Ar = OpenAr();
            _invoices.Invoice = PostedInvoice();
            await _service.RecordArPayment(7, Request(2000m));

            Assert.Equal(2, _receipts.InsertCalls.Count);
            Assert.Equal(555, _receipts.InsertCalls[0].Receipt.TransactionId);
            Assert.Equal(556, _receipts.InsertCalls[1].Receipt.TransactionId);
            Assert.Equal(1000m, _receipts.InsertCalls[0].Receipt.Amount);
            Assert.Equal(2000m, _receipts.InsertCalls[1].Receipt.Amount);
            Assert.Equal(2, _posting.Calls.Count);
        }

        // ---- Failures during the save roll everything back ----

        [Fact]
        public async Task RecordArPayment_ArChangedByAnotherPayment_ThrowsConflictAndRollsBack()
        {
            _arInvoices.UpdateRows = 0;

            await Assert.ThrowsAsync<ConflictException>(() => _service.RecordArPayment(7, Request()));

            Assert.Equal(1, _connection.Transaction!.Rollbacks);
            Assert.Equal(0, _connection.Transaction.Commits);
            Assert.Empty(_invoices.UpdateCalls);
            Assert.Empty(_posting.Calls);
            Assert.Empty(_receipts.InsertCalls);
            Assert.DoesNotContain("get-receipts", _log);
        }

        [Fact]
        public async Task RecordArPayment_SalesInvoiceNoLongerOpen_ThrowsConflictAndRollsBack()
        {
            _invoices.UpdateRows = 0;

            await Assert.ThrowsAsync<ConflictException>(() => _service.RecordArPayment(7, Request()));

            Assert.Equal(1, _connection.Transaction!.Rollbacks);
            Assert.Equal(0, _connection.Transaction.Commits);
            Assert.Empty(_posting.Calls);
            Assert.Empty(_receipts.InsertCalls);
        }

        [Fact]
        public async Task RecordArPayment_PostingFails_RollsBackWithoutReceiptOrCommit()
        {
            _posting.Throw = new InvalidOperationException("posting failed");

            await Assert.ThrowsAsync<InvalidOperationException>(() => _service.RecordArPayment(7, Request()));

            Assert.Equal(1, _connection.Transaction!.Rollbacks);
            Assert.Equal(0, _connection.Transaction.Commits);
            Assert.Empty(_receipts.InsertCalls);
            Assert.DoesNotContain("get-receipts", _log);
        }

        [Fact]
        public async Task RecordArPayment_ReceiptInsertFails_RollsBackWithoutCommit()
        {
            _receipts.Throw = new InvalidOperationException("insert failed");

            await Assert.ThrowsAsync<InvalidOperationException>(() => _service.RecordArPayment(7, Request()));

            Assert.Equal(1, _connection.Transaction!.Rollbacks);
            Assert.Equal(0, _connection.Transaction.Commits);
            Assert.DoesNotContain("get-receipts", _log);
        }

        // ---- Public method: GetPaymentHistory (ToResponseDto) ----

        [Fact]
        public async Task GetPaymentHistory_PassesIdToBothRepositories()
        {
            await _service.GetPaymentHistory(7);

            Assert.Equal(new long[] { 7 }, _arInvoices.GetByIdCalls);
            Assert.Equal(new long[] { 7 }, _receipts.HistoryCalls);
        }

        [Fact]
        public async Task GetPaymentHistory_UnknownAr_ReturnsNullAndDoesNotLoadReceipts()
        {
            _arInvoices.Ar = null;

            var result = await _service.GetPaymentHistory(999);

            Assert.Null(result);
            Assert.Empty(_receipts.HistoryCalls);
        }

        [Fact]
        public async Task GetPaymentHistory_MapsEveryArField()
        {
            var result = await _service.GetPaymentHistory(7);

            Assert.NotNull(result);
            Assert.Equal(7, result!.Id);
            Assert.Equal("AR-000007", result.Number);
            Assert.Equal("INV-000042", result.InvoiceNumber);
            Assert.Equal("Ann Buyer", result.CustomerName);
            Assert.Equal(new DateTime(2026, 12, 8), result.DueDate);
            Assert.Equal(20000m, result.Amount);
            Assert.Equal(5000m, result.Paid);
            Assert.Equal(15000m, result.Balance);
            Assert.Equal(PaymentStatus.Partial, result.Status);
        }

        [Fact]
        public async Task GetPaymentHistory_MapsEveryReceiptFieldInTheOrderReturned()
        {
            _receipts.History = new List<BankReceipt>
            {
                new()
                {
                    Id = 1, ReceiptNumber = "RCT-2026-000001", ReceiptDate = new DateTime(2026, 5, 1), Amount = 5000m,
                    PaymentMethod = PaymentMethod.Card, Reference = "POS-1", BankAccountName = "Operating", TransactionId = 100,
                    CustomerId = 3, BankAccountId = 1, Status = "Posted"
                },
                new()
                {
                    Id = 9, ReceiptNumber = "RCT-2026-000009", ReceiptDate = new DateTime(2026, 6, 1), Amount = 2500m,
                    PaymentMethod = PaymentMethod.Cash, Reference = null, BankAccountName = "Savings", TransactionId = 140
                }
            };

            var result = await _service.GetPaymentHistory(7);

            Assert.Equal(2, result!.Payments.Count);
            var first = result.Payments[0];
            Assert.Equal(1, first.Id);
            Assert.Equal("RCT-2026-000001", first.ReceiptNumber);
            Assert.Equal(new DateTime(2026, 5, 1), first.ReceiptDate);
            Assert.Equal(5000m, first.Amount);
            Assert.Equal(PaymentMethod.Card, first.PaymentMethod);
            Assert.Equal("POS-1", first.Reference);
            Assert.Equal("Operating", first.BankAccountName);
            Assert.Equal(100, first.TransactionId);
            var second = result.Payments[1];
            Assert.Equal(9, second.Id);
            Assert.Equal(PaymentMethod.Cash, second.PaymentMethod);
            Assert.Null(second.Reference);
            Assert.Equal("Savings", second.BankAccountName);
            Assert.Equal(140, second.TransactionId);
        }

        [Fact]
        public async Task GetPaymentHistory_NoReceipts_ReturnsEmptyPaymentsNotNull()
        {
            var result = await _service.GetPaymentHistory(7);

            Assert.NotNull(result);
            Assert.Empty(result!.Payments);
        }

        [Fact]
        public async Task GetPaymentHistory_IsReadOnly_NoConnectionOrWrites()
        {
            await _service.GetPaymentHistory(7);

            Assert.Equal(new[] { "get-ar", "get-receipts" }, _log);
            AssertNothingSaved();
            Assert.Empty(_accounts.GetByIdCalls);
            Assert.Empty(_invoices.GetByIdCalls);
        }
    }
}
