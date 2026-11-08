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
    // Story 14, PostInvoice. EnsureDraft, ValidateInvoice, ValidateAmountReceived, ValidateBankAccount,
    // ApplyPaymentCondition, DeterminePaymentStatus and CreateArInvoiceIfBalance are private;
    // they are tested only through PostInvoice.
    public class SalesInvoicePostInvoiceServiceTest
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
            public FakeRepositoryContext(FakeDbConnection connection) { Connection = connection; }
            public FakeDbConnection Connection { get; }
            public IDbConnection GetDatabaseConnection() => Connection;
        }

        private class FakeSalesInvoiceRepository : ISalesInvoiceRepository
        {
            private readonly List<string> _log;
            public FakeSalesInvoiceRepository(List<string> log) { _log = log; }

            public SalesInvoice? Invoice { get; set; }
            public int UpdateRows { get; set; } = 1;
            public List<long> GetByIdCalls { get; } = new();
            public List<(string Lifecycle, decimal Paid, decimal Balance, PaymentStatus? Status, IDbTransaction Tx)> UpdateCalls { get; } = new();

            public Task<SalesInvoice?> GetInvoiceById(long id)
            {
                _log.Add("get-invoice");
                GetByIdCalls.Add(id);
                return Task.FromResult(Invoice);
            }

            public Task<int> UpdateInvoicePayment(SalesInvoice invoice, IDbTransaction tx) => throw new NotSupportedException();

            public Task<int> UpdatePostedInvoice(SalesInvoice invoice, IDbTransaction tx)
            {
                _log.Add("update-invoice");
                UpdateCalls.Add((invoice.Lifecycle, invoice.Paid, invoice.Balance, invoice.PaymentStatus, tx));
                return Task.FromResult(UpdateRows);
            }

            public Task<SalesInvoiceResponseDto> AddSalesInvoice(SalesInvoiceCreateDto invoice) => throw new NotSupportedException();
            public Task<SalesInvoiceResponseDto> PostSalesInvoice(SalesInvoiceCreateDto invoice) => throw new NotSupportedException();
            public Task<PagedList<SalesInvoiceResponseDto>> GetAllSalesInvoices(SalesInvoiceParameters parameters) => throw new NotSupportedException();
            public Task<SalesInvoiceResponseDto?> GetSalesInvoiceById(long id) => throw new NotSupportedException();
            public Task<SalesInvoiceResponseDto> CancelSalesInvoice(long id, SalesInvoiceCancelDto reason) => throw new NotSupportedException();
        }

        private class FakeVehicleRepository : IVehicleRepository
        {
            private readonly List<string> _log;
            public FakeVehicleRepository(List<string> log) { _log = log; }

            public int SoldRows { get; set; } = 1;
            public List<(long VehicleId, long InvoiceId, IDbTransaction Tx)> SoldCalls { get; } = new();

            public Task<int> MarkVehicleSold(long vehicleId, long invoiceId, IDbTransaction tx)
            {
                _log.Add("mark-sold");
                SoldCalls.Add((vehicleId, invoiceId, tx));
                return Task.FromResult(SoldRows);
            }

            public Task<VehicleResponseDto> AddVehicle(VehicleCreateDto vehicle) => throw new NotSupportedException();
            public Task<bool> VinExists(string vin) => throw new NotSupportedException();
            public Task<string?> GetVendorStatus(long vendorId) => throw new NotSupportedException();
            public Task<bool> DeleteVehicle(long id) => throw new NotSupportedException();
            public Task<PagedList<VehicleResponseDto>> GetAllVehicles(VehicleParameters parameters) => throw new NotSupportedException();
            public Task<VehicleResponseDto?> GetVehicleById(long id) => throw new NotSupportedException();
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

        private class FakeArInvoiceRepository : IArInvoiceRepository
        {
            private readonly List<string> _log;
            public FakeArInvoiceRepository(List<string> log) { _log = log; }

            public long ReturnId { get; set; } = 77;
            public Exception? Throw { get; set; }
            public List<(ArInvoice Ar, IDbTransaction Tx)> Calls { get; } = new();

            public Task<ArInvoice?> GetArInvoiceById(long id) => throw new NotSupportedException();
            public Task<int> UpdateArPayment(ArInvoice ar, decimal expectedBalance, IDbTransaction tx) => throw new NotSupportedException();

            public Task<long> InsertArInvoice(ArInvoice arInvoice, IDbTransaction tx)
            {
                _log.Add("insert-ar");
                Calls.Add((arInvoice, tx));
                if (Throw != null) throw Throw;
                return Task.FromResult(ReturnId);
            }
        }

        private class FakeBankReceiptRepository : IBankReceiptRepository
        {
            private readonly List<string> _log;
            public FakeBankReceiptRepository(List<string> log) { _log = log; }

            public long ReturnId { get; set; } = 88;
            public Exception? Throw { get; set; }
            public List<(BankReceipt Receipt, IDbTransaction Tx)> Calls { get; } = new();

            public Task<IEnumerable<BankReceipt>> GetReceiptsByArInvoiceId(long arInvoiceId) => throw new NotSupportedException();

            public Task<long> InsertBankReceipt(BankReceipt receipt, IDbTransaction tx)
            {
                _log.Add("insert-receipt");
                Calls.Add((receipt, tx));
                if (Throw != null) throw Throw;
                return Task.FromResult(ReturnId);
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
        private readonly FakeSalesInvoiceRepository _invoices;
        private readonly FakeVehicleRepository _vehicles;
        private readonly FakeBankAccountRepository _accounts;
        private readonly FakeArInvoiceRepository _arInvoices;
        private readonly FakeBankReceiptRepository _receipts;
        private readonly FakePostingService _posting;
        private readonly SalesInvoiceService _service;

        public SalesInvoicePostInvoiceServiceTest()
        {
            _connection = new FakeDbConnection(_log);
            _invoices = new FakeSalesInvoiceRepository(_log) { Invoice = DraftInvoice() };
            _vehicles = new FakeVehicleRepository(_log);
            _accounts = new FakeBankAccountRepository(_log);
            _arInvoices = new FakeArInvoiceRepository(_log);
            _receipts = new FakeBankReceiptRepository(_log);
            _posting = new FakePostingService(_log);
            _service = new SalesInvoiceService(
                _invoices, _vehicles, _accounts, _arInvoices, _receipts, _posting, new FakeRepositoryContext(_connection));
        }

        private static SalesInvoice DraftInvoice(decimal total = 20000m) => new()
        {
            Id = 42,
            Number = "INV-000042",
            CustomerId = 3,
            VehicleId = 5,
            DueDate = new DateTime(2026, 12, 8),
            Total = total,
            Paid = 0m,
            Balance = total,
            PaymentStatus = null,
            Lifecycle = "draft"
        };

        private static PostInvoiceRequest Request(
            PaymentCondition condition, decimal amount, PaymentMethod? method = PaymentMethod.Card,
            string? reference = "REF-1", long? bankAccountId = 1, DateTime? receiptDate = null) => new()
        {
            PaymentCondition = condition,
            AmountReceived = amount,
            PaymentMethod = method,
            Reference = reference,
            BankAccountId = bankAccountId,
            ReceiptDate = receiptDate
        };

        private static PostInvoiceRequest PaidInFull(decimal amount = 20000m) => Request(PaymentCondition.PaidInFull, amount);
        private static PostInvoiceRequest Partial(decimal amount = 8000m) => Request(PaymentCondition.PartialPayment, amount);
        private static PostInvoiceRequest Credit() => Request(PaymentCondition.Credit, 0m, null, null, null);

        // A rejected request must save nothing: no connection, no transaction, no write.
        private void AssertNothingSaved()
        {
            Assert.DoesNotContain("open", _log);
            Assert.DoesNotContain("begin", _log);
            Assert.Empty(_invoices.UpdateCalls);
            Assert.Empty(_vehicles.SoldCalls);
            Assert.Empty(_arInvoices.Calls);
            Assert.Empty(_posting.Calls);
            Assert.Empty(_receipts.Calls);
        }

        // ---- Public method: PostInvoice ----

        [Fact]
        public async Task PostInvoice_PassesIdToRepository()
        {
            await _service.PostInvoice(42, PaidInFull());

            Assert.Equal(new long[] { 42 }, _invoices.GetByIdCalls);
        }

        [Fact]
        public async Task PostInvoice_UnknownInvoice_ReturnsNullAndSavesNothing()
        {
            _invoices.Invoice = null;

            var result = await _service.PostInvoice(999, PaidInFull());

            Assert.Null(result);
            Assert.Equal(new long[] { 999 }, _invoices.GetByIdCalls);
            Assert.Empty(_accounts.GetByIdCalls);
            AssertNothingSaved();
        }

        [Fact]
        public async Task PostInvoice_ReturnsPostedInvoiceWithPaidBalanceStatusAndLinks()
        {
            var result = await _service.PostInvoice(42, Partial(8000m));

            Assert.NotNull(result);
            Assert.Equal(42, result!.Id);
            Assert.Equal("INV-000042", result.Number);
            Assert.Equal(3, result.Customer_Id);
            Assert.Equal(5, result.Vehicle_Id);
            Assert.Equal(new DateTime(2026, 12, 8), result.Due_Date);
            Assert.Equal(20000m, result.Total);
            Assert.Equal(8000m, result.Paid);
            Assert.Equal(12000m, result.Balance);
            Assert.Equal(InvoiceLifecycle.Posted, result.Lifecycle);
            Assert.Equal(PaymentStatus.Partial, result.Payment_Status);
            Assert.Equal(77, result.Ar_Invoice_Id);
            Assert.Equal(88, result.Bank_Receipt_Id);
        }

        // ---- EnsureDraft ----

        [Theory]
        [InlineData("posted")]
        [InlineData("cancelled")]
        public async Task PostInvoice_NotDraft_ThrowsValidationAndSavesNothing(string lifecycle)
        {
            _invoices.Invoice!.Lifecycle = lifecycle;

            var ex = await Assert.ThrowsAsync<ValidationException>(() => _service.PostInvoice(42, PaidInFull()));

            Assert.Contains("Draft", ex.Message);
            AssertNothingSaved();
        }

        [Fact]
        public async Task PostInvoice_DraftInAnyCasing_IsPosted()
        {
            _invoices.Invoice!.Lifecycle = "Draft";

            var result = await _service.PostInvoice(42, PaidInFull());

            Assert.Equal(InvoiceLifecycle.Posted, result!.Lifecycle);
        }

        // ---- ValidateInvoice (Story 12 rules, run again) ----

        [Fact]
        public async Task PostInvoice_TotalZero_ThrowsValidationAndSavesNothing()
        {
            _invoices.Invoice = DraftInvoice(0m);

            var ex = await Assert.ThrowsAsync<ValidationException>(() => _service.PostInvoice(42, Credit()));

            Assert.Contains("total must be greater than zero", ex.Message);
            AssertNothingSaved();
        }

        [Fact]
        public async Task PostInvoice_MissingCustomer_ThrowsValidation()
        {
            _invoices.Invoice!.CustomerId = 0;

            var ex = await Assert.ThrowsAsync<ValidationException>(() => _service.PostInvoice(42, PaidInFull()));

            Assert.Contains("Customer is required", ex.Message);
            AssertNothingSaved();
        }

        [Fact]
        public async Task PostInvoice_MissingVehicle_ThrowsValidation()
        {
            _invoices.Invoice!.VehicleId = 0;

            var ex = await Assert.ThrowsAsync<ValidationException>(() => _service.PostInvoice(42, PaidInFull()));

            Assert.Contains("Vehicle is required", ex.Message);
            AssertNothingSaved();
        }

        // ---- ValidateAmountReceived: AC 5 ----

        [Fact]
        public async Task PostInvoice_NegativeAmount_ThrowsValidationAndSavesNothing()
        {
            var ex = await Assert.ThrowsAsync<ValidationException>(
                () => _service.PostInvoice(42, Request(PaymentCondition.PartialPayment, -1m)));

            Assert.Contains("cannot be negative", ex.Message);
            AssertNothingSaved();
        }

        [Fact]
        public async Task PostInvoice_AmountAboveTotal_ThrowsValidationAndSavesNothing()
        {
            var ex = await Assert.ThrowsAsync<ValidationException>(
                () => _service.PostInvoice(42, Request(PaymentCondition.PaidInFull, 20000.01m)));

            Assert.Contains("cannot exceed the invoice total", ex.Message);
            AssertNothingSaved();
        }

        [Fact]
        public async Task PostInvoice_AmountEqualToTotal_IsAccepted()
        {
            var result = await _service.PostInvoice(42, PaidInFull(20000m));

            Assert.Equal(20000m, result!.Paid);
        }

        // ---- ValidateAmountReceived: amount must fit the condition (AC 1-3) ----

        [Theory]
        [InlineData(19999.99)]
        [InlineData(1)]
        [InlineData(0)]
        public async Task PostInvoice_PaidInFullWithAmountBelowTotal_ThrowsValidation(double amount)
        {
            var ex = await Assert.ThrowsAsync<ValidationException>(
                () => _service.PostInvoice(42, Request(PaymentCondition.PaidInFull, (decimal)amount)));

            Assert.Contains("paid-in-full", ex.Message);
            AssertNothingSaved();
        }

        [Theory]
        [InlineData(0)]
        [InlineData(20000)]
        public async Task PostInvoice_PartialPaymentOutsideZeroToTotal_ThrowsValidation(double amount)
        {
            var ex = await Assert.ThrowsAsync<ValidationException>(
                () => _service.PostInvoice(42, Request(PaymentCondition.PartialPayment, (decimal)amount)));

            Assert.Contains("partial payment", ex.Message);
            AssertNothingSaved();
        }

        [Theory]
        [InlineData(0.01)]
        [InlineData(19999.99)]
        public async Task PostInvoice_PartialPaymentBoundaries_AreAccepted(double amount)
        {
            var result = await _service.PostInvoice(42, Partial((decimal)amount));

            Assert.Equal(PaymentStatus.Partial, result!.Payment_Status);
        }

        [Fact]
        public async Task PostInvoice_CreditWithAmount_ThrowsValidation()
        {
            var ex = await Assert.ThrowsAsync<ValidationException>(
                () => _service.PostInvoice(42, Request(PaymentCondition.Credit, 100m)));

            Assert.Contains("credit sale cannot receive an amount", ex.Message);
            AssertNothingSaved();
        }

        [Fact]
        public async Task PostInvoice_UnknownPaymentCondition_ThrowsValidation()
        {
            var ex = await Assert.ThrowsAsync<ValidationException>(
                () => _service.PostInvoice(42, Request((PaymentCondition)99, 0m)));

            Assert.Contains("Payment condition must be", ex.Message);
            AssertNothingSaved();
        }

        // ---- ValidateAmountReceived: method (AC 6) and reference ----

        [Theory]
        [InlineData(PaymentMethod.Cash)]
        [InlineData(PaymentMethod.Card)]
        [InlineData(PaymentMethod.Transfer)]
        [InlineData(PaymentMethod.Check)]
        public async Task PostInvoice_EachAllowedMethod_IsAcceptedAndStoredOnReceipt(PaymentMethod method)
        {
            await _service.PostInvoice(42, Request(PaymentCondition.PaidInFull, 20000m, method));

            Assert.Equal(method, Assert.Single(_receipts.Calls).Receipt.PaymentMethod);
        }

        [Fact]
        public async Task PostInvoice_MethodMissingWhenAmountReceived_ThrowsValidation()
        {
            var ex = await Assert.ThrowsAsync<ValidationException>(
                () => _service.PostInvoice(42, Request(PaymentCondition.PaidInFull, 20000m, method: null)));

            Assert.Contains("Cash, Card, Transfer or Check", ex.Message);
            AssertNothingSaved();
        }

        [Fact]
        public async Task PostInvoice_UnknownMethod_ThrowsValidation()
        {
            var ex = await Assert.ThrowsAsync<ValidationException>(
                () => _service.PostInvoice(42, Request(PaymentCondition.PaidInFull, 20000m, (PaymentMethod)99)));

            Assert.Contains("Cash, Card, Transfer or Check", ex.Message);
            AssertNothingSaved();
        }

        [Fact]
        public async Task PostInvoice_CreditWithoutMethod_IsAccepted()
        {
            var result = await _service.PostInvoice(42, Credit());

            Assert.Equal(PaymentStatus.Unpaid, result!.Payment_Status);
        }

        [Fact]
        public async Task PostInvoice_ReferenceOver100Characters_ThrowsValidation()
        {
            var ex = await Assert.ThrowsAsync<ValidationException>(
                () => _service.PostInvoice(42, Request(PaymentCondition.PaidInFull, 20000m, reference: new string('R', 101))));

            Assert.Contains("Reference cannot exceed 100 characters", ex.Message);
            AssertNothingSaved();
        }

        [Fact]
        public async Task PostInvoice_ReferenceOf100Characters_IsAccepted()
        {
            await _service.PostInvoice(42, Request(PaymentCondition.PaidInFull, 20000m, reference: new string('R', 100)));

            Assert.Equal(100, Assert.Single(_receipts.Calls).Receipt.Reference!.Length);
        }

        [Fact]
        public async Task PostInvoice_ReferenceIsTrimmedOnReceipt()
        {
            await _service.PostInvoice(42, Request(PaymentCondition.PaidInFull, 20000m, reference: "  WIRE-77  "));

            Assert.Equal("WIRE-77", Assert.Single(_receipts.Calls).Receipt.Reference);
        }

        [Fact]
        public async Task PostInvoice_NoReference_StoresNullOnReceipt()
        {
            await _service.PostInvoice(42, Request(PaymentCondition.PaidInFull, 20000m, reference: null));

            Assert.Null(Assert.Single(_receipts.Calls).Receipt.Reference);
        }

        // ---- ValidateBankAccount: AC 7 ----

        [Fact]
        public async Task PostInvoice_AmountReceivedWithoutBankAccountId_ThrowsValidation()
        {
            var ex = await Assert.ThrowsAsync<ValidationException>(
                () => _service.PostInvoice(42, Request(PaymentCondition.PaidInFull, 20000m, bankAccountId: null)));

            Assert.Contains("bank account is required", ex.Message);
            Assert.Empty(_accounts.GetByIdCalls);
            AssertNothingSaved();
        }

        [Fact]
        public async Task PostInvoice_BankAccountDoesNotExist_ThrowsValidation()
        {
            _accounts.Account = null;

            var ex = await Assert.ThrowsAsync<ValidationException>(
                () => _service.PostInvoice(42, Request(PaymentCondition.PaidInFull, 20000m, bankAccountId: 9)));

            Assert.Contains("bank account is required", ex.Message);
            Assert.Equal(new long[] { 9 }, _accounts.GetByIdCalls);
            AssertNothingSaved();
        }

        [Fact]
        public async Task PostInvoice_InactiveBankAccount_ThrowsValidation()
        {
            _accounts.Account = new BankAccountResponseDto { Id = 1, Status = Status.Inactive };

            var ex = await Assert.ThrowsAsync<ValidationException>(() => _service.PostInvoice(42, PaidInFull()));

            Assert.Contains("must be active", ex.Message);
            AssertNothingSaved();
        }

        [Fact]
        public async Task PostInvoice_BankAccountWithoutStatus_IsRejected()
        {
            _accounts.Account = new BankAccountResponseDto { Id = 1, Status = null };

            await Assert.ThrowsAsync<ValidationException>(() => _service.PostInvoice(42, PaidInFull()));

            AssertNothingSaved();
        }

        [Fact]
        public async Task PostInvoice_ActiveBankAccount_IsLookedUpByRequestId()
        {
            await _service.PostInvoice(42, Request(PaymentCondition.PaidInFull, 20000m, bankAccountId: 6));

            Assert.Equal(new long[] { 6 }, _accounts.GetByIdCalls);
        }

        [Fact]
        public async Task PostInvoice_Credit_DoesNotLookUpOrRequireBankAccount()
        {
            _accounts.Account = null;

            var result = await _service.PostInvoice(42, Credit());

            Assert.NotNull(result);
            Assert.Empty(_accounts.GetByIdCalls);
        }

        // ---- ApplyPaymentCondition and DeterminePaymentStatus: AC 1-3 ----

        [Fact]
        public async Task PostInvoice_PaidInFull_PaidEqualsTotalBalanceZeroStatusPaid()
        {
            var result = await _service.PostInvoice(42, PaidInFull(20000m));

            Assert.Equal(20000m, result!.Paid);
            Assert.Equal(0m, result.Balance);
            Assert.Equal(PaymentStatus.Paid, result.Payment_Status);
        }

        [Fact]
        public async Task PostInvoice_PartialPayment_BalanceIsTotalMinusPaidStatusPartial()
        {
            var result = await _service.PostInvoice(42, Partial(8000.50m));

            Assert.Equal(8000.50m, result!.Paid);
            Assert.Equal(11999.50m, result.Balance);
            Assert.Equal(PaymentStatus.Partial, result.Payment_Status);
        }

        [Fact]
        public async Task PostInvoice_Credit_PaidZeroBalanceTotalStatusUnpaid()
        {
            var result = await _service.PostInvoice(42, Credit());

            Assert.Equal(0m, result!.Paid);
            Assert.Equal(20000m, result.Balance);
            Assert.Equal(PaymentStatus.Unpaid, result.Payment_Status);
        }

        [Fact]
        public async Task PostInvoice_BalanceUsesInvoiceTotal_NotAFixedAmount()
        {
            _invoices.Invoice = DraftInvoice(12345.67m);

            var result = await _service.PostInvoice(42, Request(PaymentCondition.PartialPayment, 345.67m));

            Assert.Equal(12000m, result!.Balance);
        }

        [Fact]
        public async Task PostInvoice_SavesPostedLifecyclePaidBalanceAndStatusOnTheInvoice()
        {
            await _service.PostInvoice(42, Partial(8000m));

            var call = Assert.Single(_invoices.UpdateCalls);
            Assert.Equal("posted", call.Lifecycle);
            Assert.Equal(8000m, call.Paid);
            Assert.Equal(12000m, call.Balance);
            Assert.Equal(PaymentStatus.Partial, call.Status);
        }

        // ---- CreateArInvoiceIfBalance: AC 1-4 ----

        [Fact]
        public async Task PostInvoice_PaidInFull_CreatesNoArInvoice()
        {
            var result = await _service.PostInvoice(42, PaidInFull());

            Assert.Empty(_arInvoices.Calls);
            Assert.Null(result!.Ar_Invoice_Id);
        }

        [Fact]
        public async Task PostInvoice_Partial_CreatesArInvoiceMatchingTheSalesInvoice()
        {
            var result = await _service.PostInvoice(42, Partial(8000m));

            var (ar, _) = Assert.Single(_arInvoices.Calls);
            Assert.Equal(42, ar.InvoiceId);
            Assert.Equal(20000m, ar.Amount);
            Assert.Equal(8000m, ar.Paid);
            Assert.Equal(12000m, ar.Balance);
            Assert.Equal(PaymentStatus.Partial, ar.Status);
            Assert.Equal(new DateTime(2026, 12, 8), ar.DueDate);
            Assert.Equal("system", ar.CreatedBy);
            Assert.InRange(ar.Date, DateTime.UtcNow.Date.AddDays(-1), DateTime.UtcNow.Date.AddDays(1));
            Assert.Equal(77, result!.Ar_Invoice_Id);
        }

        [Fact]
        public async Task PostInvoice_Credit_CreatesArInvoiceForTheFullTotal()
        {
            var result = await _service.PostInvoice(42, Credit());

            var (ar, _) = Assert.Single(_arInvoices.Calls);
            Assert.Equal(20000m, ar.Amount);
            Assert.Equal(0m, ar.Paid);
            Assert.Equal(20000m, ar.Balance);
            Assert.Equal(PaymentStatus.Unpaid, ar.Status);
            Assert.Equal(77, result!.Ar_Invoice_Id);
        }

        [Fact]
        public async Task PostInvoice_ArInvoiceWithoutDueDate_HasNoDueDate()
        {
            _invoices.Invoice!.DueDate = null;

            await _service.PostInvoice(42, Credit());

            Assert.Null(Assert.Single(_arInvoices.Calls).Ar.DueDate);
        }

        // ---- Bank Receipt and Transaction: AC 1-3, 8 ----

        [Fact]
        public async Task PostInvoice_PaidInFull_PostsOneIncomingBankReceiptTransaction()
        {
            await _service.PostInvoice(42, Request(PaymentCondition.PaidInFull, 20000m, bankAccountId: 6));

            var (request, _) = Assert.Single(_posting.Calls);
            Assert.Equal(6, request.BankAccountId);
            Assert.Equal(Direction.In, request.Direction);
            Assert.Equal(20000m, request.Amount);
            Assert.Equal(TransactionType.BankReceipt, request.Type);
            Assert.Equal("INV-000042", request.SourceNumber);
            Assert.Equal(TransactionStatus.Posted, request.Status);
        }

        [Fact]
        public async Task PostInvoice_PaidInFull_SavesOneReceiptLinkedToInvoiceWithoutAr()
        {
            var result = await _service.PostInvoice(42, Request(PaymentCondition.PaidInFull, 20000m, PaymentMethod.Check, "CHK-1001", 6));

            var (receipt, _) = Assert.Single(_receipts.Calls);
            Assert.Equal(555, receipt.TransactionId);
            Assert.Equal(6, receipt.BankAccountId);
            Assert.Equal(3, receipt.CustomerId);
            Assert.Equal(42, receipt.SalesInvoiceId);
            Assert.Null(receipt.ArInvoiceId);
            Assert.Equal(PaymentMethod.Check, receipt.PaymentMethod);
            Assert.Equal("CHK-1001", receipt.Reference);
            Assert.Equal(20000m, receipt.Amount);
            Assert.Equal("Posted", receipt.Status);
            Assert.Equal("system", receipt.CreatedBy);
            Assert.Equal(88, result!.Bank_Receipt_Id);
        }

        [Fact]
        public async Task PostInvoice_Partial_ReceiptLinksToBothSalesInvoiceAndArInvoice()
        {
            await _service.PostInvoice(42, Partial(8000m));

            var (receipt, _) = Assert.Single(_receipts.Calls);
            Assert.Equal(42, receipt.SalesInvoiceId);
            Assert.Equal(77, receipt.ArInvoiceId);
            Assert.Equal(8000m, receipt.Amount);
        }

        [Fact]
        public async Task PostInvoice_Credit_CreatesNoReceiptAndNoTransaction()
        {
            var result = await _service.PostInvoice(42, Credit());

            Assert.Empty(_posting.Calls);
            Assert.Empty(_receipts.Calls);
            Assert.Null(result!.Bank_Receipt_Id);
        }

        [Fact]
        public async Task PostInvoice_ReceiptDateGiven_UsedOnTransactionAndReceipt()
        {
            var date = new DateTime(2026, 11, 8);

            await _service.PostInvoice(42, Request(PaymentCondition.PaidInFull, 20000m, receiptDate: date));

            Assert.Equal(date, Assert.Single(_posting.Calls).Request.Date);
            Assert.Equal(date, Assert.Single(_receipts.Calls).Receipt.ReceiptDate);
        }

        [Fact]
        public async Task PostInvoice_NoReceiptDate_DefaultsToNow()
        {
            await _service.PostInvoice(42, PaidInFull());

            var from = DateTime.UtcNow.AddMinutes(-5);
            var to = DateTime.UtcNow.AddMinutes(1);
            Assert.InRange(Assert.Single(_posting.Calls).Request.Date, from, to);
            Assert.InRange(Assert.Single(_receipts.Calls).Receipt.ReceiptDate, from, to);
        }

        // ---- Save together: AC 8 ----

        [Fact]
        public async Task PostInvoice_SavesEverythingInOneTransactionInOrderAndCommitsOnce()
        {
            await _service.PostInvoice(42, Partial(8000m));

            Assert.Equal(
                new[]
                {
                    "get-invoice", "get-account", "open", "begin", "update-invoice", "mark-sold",
                    "insert-ar", "post-transaction", "insert-receipt", "commit"
                },
                _log);
            Assert.Equal(1, _connection.Transaction!.Commits);
            Assert.Equal(0, _connection.Transaction.Rollbacks);
        }

        [Fact]
        public async Task PostInvoice_EveryWriteUsesTheSameTransaction()
        {
            await _service.PostInvoice(42, Partial(8000m));

            var tx = _connection.Transaction!;
            Assert.Same(tx, Assert.Single(_invoices.UpdateCalls).Tx);
            Assert.Same(tx, Assert.Single(_vehicles.SoldCalls).Tx);
            Assert.Same(tx, Assert.Single(_arInvoices.Calls).Tx);
            Assert.Same(tx, Assert.Single(_posting.Calls).Tx);
            Assert.Same(tx, Assert.Single(_receipts.Calls).Tx);
        }

        [Fact]
        public async Task PostInvoice_MarksTheInvoiceVehicleSoldWithTheInvoiceId()
        {
            await _service.PostInvoice(42, PaidInFull());

            var call = Assert.Single(_vehicles.SoldCalls);
            Assert.Equal(5, call.VehicleId);
            Assert.Equal(42, call.InvoiceId);
        }

        [Fact]
        public async Task PostInvoice_InvoiceNoLongerDraft_ThrowsConflictAndRollsBack()
        {
            _invoices.UpdateRows = 0;

            var ex = await Assert.ThrowsAsync<ConflictException>(() => _service.PostInvoice(42, Partial()));

            Assert.Contains("no longer a draft", ex.Message);
            Assert.Equal(1, _connection.Transaction!.Rollbacks);
            Assert.Equal(0, _connection.Transaction.Commits);
            Assert.Empty(_vehicles.SoldCalls);
            Assert.Empty(_arInvoices.Calls);
            Assert.Empty(_posting.Calls);
            Assert.Empty(_receipts.Calls);
        }

        [Fact]
        public async Task PostInvoice_VehicleAlreadySold_ThrowsConflictRollsBackAndCreatesNothingElse()
        {
            _vehicles.SoldRows = 0;

            var ex = await Assert.ThrowsAsync<ConflictException>(() => _service.PostInvoice(42, Partial()));

            Assert.Contains("already sold", ex.Message);
            Assert.Equal(1, _connection.Transaction!.Rollbacks);
            Assert.Equal(0, _connection.Transaction.Commits);
            Assert.Empty(_arInvoices.Calls);
            Assert.Empty(_posting.Calls);
            Assert.Empty(_receipts.Calls);
        }

        [Fact]
        public async Task PostInvoice_ArInsertFails_RollsBackAndPostsNoTransactionOrReceipt()
        {
            _arInvoices.Throw = new InvalidOperationException("ar failed");

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _service.PostInvoice(42, Partial()));

            Assert.Equal("ar failed", ex.Message);
            Assert.Equal(1, _connection.Transaction!.Rollbacks);
            Assert.Equal(0, _connection.Transaction.Commits);
            Assert.Empty(_posting.Calls);
            Assert.Empty(_receipts.Calls);
        }

        [Fact]
        public async Task PostInvoice_PostTransactionFails_RollsBackAndInsertsNoReceipt()
        {
            _posting.Throw = new ValidationException("posting failed");

            var ex = await Assert.ThrowsAsync<ValidationException>(() => _service.PostInvoice(42, Partial()));

            Assert.Equal("posting failed", ex.Message);
            Assert.Equal(1, _connection.Transaction!.Rollbacks);
            Assert.Equal(0, _connection.Transaction.Commits);
            Assert.Empty(_receipts.Calls);
        }

        [Fact]
        public async Task PostInvoice_ReceiptInsertFails_RollsBackAndDoesNotCommit()
        {
            _receipts.Throw = new InvalidOperationException("receipt failed");

            await Assert.ThrowsAsync<InvalidOperationException>(() => _service.PostInvoice(42, PaidInFull()));

            Assert.Equal(1, _connection.Transaction!.Rollbacks);
            Assert.Equal(0, _connection.Transaction.Commits);
        }

        [Fact]
        public async Task PostInvoice_Credit_StillRunsInOneTransactionWithInvoiceVehicleAndAr()
        {
            await _service.PostInvoice(42, Credit());

            Assert.Equal(
                new[] { "get-invoice", "open", "begin", "update-invoice", "mark-sold", "insert-ar", "commit" },
                _log);
        }
    }
}
