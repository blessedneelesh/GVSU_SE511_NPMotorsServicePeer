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
    // Story 20, RecordVendorPayment and GetPaymentHistory. EnsureBillOpen, ValidatePaymentAmount, ValidateBankAccount,
    // ApplyPayment, DeterminePaymentStatus and RecalculateVendorBalance are private; they are tested only through the
    // two public methods. Base data: AP bill amount 40000, paid 15000, balance 25000 (partial); bank account book balance 50000.
    public class APBillServiceTest
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

        private class FakeApBillRepository : IAPBillRepository
        {
            private readonly List<string> _log;
            public FakeApBillRepository(List<string> log) { _log = log; }

            public APBill? Bill { get; set; }
            public int UpdateRows { get; set; } = 1;
            public decimal OpenBalance { get; set; } = 17500m;
            public List<(long Id, IDbTransaction? Tx)> GetByIdCalls { get; } = new();
            // State of the bill at the moment UpdateApBillPayment was called.
            public List<(decimal Amount, decimal Paid, decimal Balance, string Status, IDbTransaction Tx)> UpdateCalls { get; } = new();
            public List<(long VendorId, IDbTransaction Tx)> OpenBalanceCalls { get; } = new();

            public Task<APBill?> GetApBillById(long id, IDbTransaction? tx)
            {
                _log.Add("get-bill");
                GetByIdCalls.Add((id, tx));
                return Task.FromResult(Bill);
            }

            public Task<int> UpdateApBillPayment(APBill bill, IDbTransaction tx)
            {
                _log.Add("update-bill");
                UpdateCalls.Add((bill.Amount, bill.Paid, bill.Balance, bill.Status, tx));
                return Task.FromResult(UpdateRows);
            }

            public Task<decimal> GetOpenBalanceByVendor(long vendorId, IDbTransaction tx)
            {
                _log.Add("get-open-balance");
                OpenBalanceCalls.Add((vendorId, tx));
                return Task.FromResult(OpenBalance);
            }

            public PagedList<ApAgingDto> Page { get; set; } = new(new List<ApAgingDto>(), 0, 1, 5);
            public List<ApAgingParameters> AllApCalls { get; } = new();

            public Task<PagedList<ApAgingDto>> GetAllAp(ApAgingParameters parameters)
            {
                _log.Add("get-all-ap");
                AllApCalls.Add(parameters);
                return Task.FromResult(Page);
            }
        }

        private class FakeBankPaymentRepository : IBankPaymentRepository
        {
            private readonly List<string> _log;
            public FakeBankPaymentRepository(List<string> log) { _log = log; }

            public long NextId { get; set; } = 88;
            public Exception? Throw { get; set; }
            public List<BankPayment> History { get; set; } = new();
            public List<(BankPayment Payment, IDbTransaction Tx)> InsertCalls { get; } = new();
            public List<long> HistoryCalls { get; } = new();

            public Task<long> InsertBankPayment(BankPayment payment, IDbTransaction tx)
            {
                _log.Add("insert-payment");
                InsertCalls.Add((payment, tx));
                if (Throw != null) throw Throw;
                return Task.FromResult(NextId++);
            }

            public Task<IEnumerable<BankPayment>> GetPaymentsByBillId(long apBillId)
            {
                _log.Add("get-payments");
                HistoryCalls.Add(apBillId);
                return Task.FromResult<IEnumerable<BankPayment>>(History);
            }
        }

        private class FakeBankAccountRepository : IBankAccountRepository
        {
            private readonly List<string> _log;
            public FakeBankAccountRepository(List<string> log) { _log = log; }

            public BankAccountResponseDto? Account { get; set; } = new() { Id = 1, BookBalance = 50000m, Status = Status.Active };
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

        private class FakeVendorRepository : IVendorRepository
        {
            private readonly List<string> _log;
            public FakeVendorRepository(List<string> log) { _log = log; }

            public VendorResponseDto? Vendor { get; set; } = new() { Id = 3, Name = "Acme Motors", Balance = 17500m };
            public List<(long VendorId, decimal Balance, IDbTransaction Tx)> UpdateCalls { get; } = new();
            public List<long> GetByIdCalls { get; } = new();

            public Task<int> UpdateVendorBalance(long vendorId, decimal balance, IDbTransaction tx)
            {
                _log.Add("update-vendor");
                UpdateCalls.Add((vendorId, balance, tx));
                return Task.FromResult(1);
            }

            public Task<VendorResponseDto?> GetVendorById(long id)
            {
                _log.Add("get-vendor");
                GetByIdCalls.Add(id);
                return Task.FromResult(Vendor);
            }

            public Task<PagedList<VendorResponseDto>> GetAllVendors(VendorParameters p) => throw new NotSupportedException();
            public Task<IEnumerable<VendorResponseDto>> GetActiveVendors(VendorType? type = null) => throw new NotSupportedException();
            public Task<VendorResponseDto> AddVendor(VendorCreateDto vendor) => throw new NotSupportedException();
            public Task<VendorResponseDto?> UpdateVendor(long id, VendorUpdateDto vendor) => throw new NotSupportedException();
            public Task<bool> DeleteVendor(long id) => throw new NotSupportedException();
            public Task<bool> IsVendorReferenced(long id) => throw new NotSupportedException();
        }

        private class FakePostingService : ITransactionPostingService
        {
            private readonly List<string> _log;
            public FakePostingService(List<string> log) { _log = log; }

            public long NextId { get; set; } = 555;
            public Exception? Throw { get; set; }
            public List<(PostTransactionRequest Request, IDbTransaction? Tx)> Calls { get; } = new();

            public Task<long> PostTransaction(PostTransactionRequest request, IDbTransaction? tx = null)
            {
                _log.Add("post-transaction");
                Calls.Add((request, tx));
                if (Throw != null) throw Throw;
                return Task.FromResult(NextId++);
            }
        }

        private readonly FakeDbConnection _connection;
        private readonly FakeApBillRepository _bills;
        private readonly FakeBankPaymentRepository _payments;
        private readonly FakeBankAccountRepository _accounts;
        private readonly FakeVendorRepository _vendors;
        private readonly FakePostingService _posting;
        private readonly APBillService _service;

        public APBillServiceTest()
        {
            _connection = new FakeDbConnection(_log);
            _bills = new FakeApBillRepository(_log) { Bill = OpenBill() };
            _payments = new FakeBankPaymentRepository(_log);
            _accounts = new FakeBankAccountRepository(_log);
            _vendors = new FakeVendorRepository(_log);
            _posting = new FakePostingService(_log);
            _service = new APBillService(
                _bills, _payments, _accounts, _vendors, _posting, new FakeRepositoryContext(_connection));
        }

        private static APBill OpenBill(decimal amount = 40000m, decimal paid = 15000m, string status = "partial") => new()
        {
            Id = 7,
            Number = "AP-2026-000007",
            VendorId = 3,
            VehicleId = 9,
            Date = new DateTime(2026, 10, 1),
            DueDate = new DateTime(2026, 12, 1),
            Amount = amount,
            Paid = paid,
            Balance = amount - paid,
            Status = status
        };

        private static VendorPaymentCreateDto Payment(
            decimal amount = 10000m, string method = "Transfer", string? reference = "WIRE-9",
            long bankAccountId = 1, DateTime? date = null) => new()
        {
            Payment_Date = date ?? new DateTime(2026, 11, 8),
            Amount = amount,
            Payment_Method = method,
            Reference = reference,
            Bank_Account_Id = bankAccountId
        };

        // A rejected payment must save nothing: no posting, no payment row, no bill update, no vendor update, no commit.
        private void AssertNothingSaved()
        {
            Assert.Empty(_posting.Calls);
            Assert.Empty(_payments.InsertCalls);
            Assert.Empty(_bills.UpdateCalls);
            Assert.Empty(_vendors.UpdateCalls);
            Assert.DoesNotContain("commit", _log);
        }

        // ---- Public method: RecordVendorPayment ----

        [Fact]
        public async Task RecordVendorPayment_LoadsTheBillByIdInsideTheTransaction()
        {
            await _service.RecordVendorPayment(7, Payment());

            var first = _bills.GetByIdCalls[0];
            Assert.Equal(7, first.Id);
            Assert.NotNull(first.Tx);
            Assert.Same(_connection.Transaction, first.Tx);
        }

        [Fact]
        public async Task RecordVendorPayment_UnknownBill_ReturnsNullRollsBackAndSavesNothing()
        {
            _bills.Bill = null;

            var result = await _service.RecordVendorPayment(999, Payment());

            Assert.Null(result);
            Assert.Equal(999, _bills.GetByIdCalls[0].Id);
            Assert.Empty(_accounts.GetByIdCalls);
            Assert.Contains("rollback", _log);
            AssertNothingSaved();
        }

        [Fact]
        public async Task RecordVendorPayment_Success_CallsInTheDocumentedOrderAndCommitsOnce()
        {
            await _service.RecordVendorPayment(7, Payment());

            var beforeResponse = _log.Take(_log.IndexOf("commit") + 1).ToList();
            Assert.Equal(new[]
            {
                "open", "begin", "get-bill", "get-account", "post-transaction", "insert-payment",
                "update-bill", "get-open-balance", "update-vendor", "commit"
            }, beforeResponse);
            Assert.Equal(1, _connection.Transaction!.Commits);
            Assert.Equal(0, _connection.Transaction.Rollbacks);
        }

        [Fact]
        public async Task RecordVendorPayment_EveryWriteUsesTheSameTransaction()
        {
            await _service.RecordVendorPayment(7, Payment());

            var tx = _connection.Transaction;
            Assert.Same(tx, _posting.Calls[0].Tx);
            Assert.Same(tx, _payments.InsertCalls[0].Tx);
            Assert.Same(tx, _bills.UpdateCalls[0].Tx);
            Assert.Same(tx, _bills.OpenBalanceCalls[0].Tx);
            Assert.Same(tx, _vendors.UpdateCalls[0].Tx);
        }

        [Fact]
        public async Task RecordVendorPayment_PassesBankAccountIdToRepository()
        {
            await _service.RecordVendorPayment(7, Payment(bankAccountId: 12));

            Assert.Equal(new long[] { 12 }, _accounts.GetByIdCalls);
        }

        [Fact]
        public async Task RecordVendorPayment_PostsAnOutBankPaymentTransaction()
        {
            await _service.RecordVendorPayment(7, Payment(10000m, "Check", "CHK-1", 1, new DateTime(2026, 11, 8)));

            var request = Assert.Single(_posting.Calls).Request;
            Assert.Equal(1, request.BankAccountId);
            Assert.Equal(Direction.Out, request.Direction);
            Assert.Equal(10000m, request.Amount);
            Assert.Equal(new DateTime(2026, 11, 8), request.Date);
            Assert.Equal(TransactionType.BankPayment, request.Type);
            Assert.Equal("AP-2026-000007", request.SourceNumber);
            Assert.Equal(TransactionStatus.Posted, request.Status);
        }

        [Fact]
        public async Task RecordVendorPayment_InsertsABankPaymentLinkedToTheBillVendorAndTransaction()
        {
            _posting.NextId = 555;

            await _service.RecordVendorPayment(7, Payment(10000m, "Check", "  CHK-1  ", 1, new DateTime(2026, 11, 8)));

            var inserted = Assert.Single(_payments.InsertCalls).Payment;
            Assert.Equal(555, inserted.TransactionId);
            Assert.Equal(1, inserted.BankAccountId);
            Assert.Equal(3, inserted.VendorId);
            Assert.Equal(7, inserted.ApBillId);
            Assert.Equal(9, inserted.VehicleId);
            Assert.Equal(new DateTime(2026, 11, 8), inserted.PaymentDate);
            Assert.Equal("Check", inserted.PaymentMethod);
            Assert.Equal("CHK-1", inserted.Reference);
            Assert.Equal(10000m, inserted.Amount);
            Assert.Equal("Posted", inserted.Status);
        }

        [Fact]
        public async Task RecordVendorPayment_NoReference_InsertsNullReference()
        {
            await _service.RecordVendorPayment(7, Payment(reference: null));

            Assert.Null(_payments.InsertCalls[0].Payment.Reference);
        }

        // AC 3, 7: each installment is a separate bank payment with its own transaction; nothing earlier is rewritten.
        [Fact]
        public async Task RecordVendorPayment_TwoPayments_InsertTwoSeparatePaymentsWithTheirOwnTransactions()
        {
            _posting.NextId = 555;

            await _service.RecordVendorPayment(7, Payment(5000m));
            await _service.RecordVendorPayment(7, Payment(6000m));

            Assert.Equal(2, _payments.InsertCalls.Count);
            Assert.Equal(new long[] { 555, 556 }, _payments.InsertCalls.Select(c => c.Payment.TransactionId).ToArray());
            Assert.Equal(new[] { 5000m, 6000m }, _payments.InsertCalls.Select(c => c.Payment.Amount).ToArray());
            Assert.Equal(2, _posting.Calls.Count);
        }

        // ---- ApplyPayment / DeterminePaymentStatus (through RecordVendorPayment) ----

        [Fact]
        public async Task RecordVendorPayment_PartialInstallment_UpdatesPaidBalanceAndStatusTogether()
        {
            // Diagram example: bill 40000, paid 15000, balance 25000; pay 10000 -> paid 25000, balance 15000, "partial".
            await _service.RecordVendorPayment(7, Payment(10000m));

            var update = Assert.Single(_bills.UpdateCalls);
            Assert.Equal(25000m, update.Paid);
            Assert.Equal(15000m, update.Balance);
            Assert.Equal("partial", update.Status);
        }

        [Fact]
        public async Task RecordVendorPayment_PayingTheWholeBalance_MarksPaidWithZeroBalance()
        {
            // Diagram example: pay 15000 on a bill at paid 25000 -> paid 40000, balance 0, "paid".
            _bills.Bill = OpenBill(40000m, 25000m);

            await _service.RecordVendorPayment(7, Payment(15000m));

            var update = Assert.Single(_bills.UpdateCalls);
            Assert.Equal(40000m, update.Paid);
            Assert.Equal(0m, update.Balance);
            Assert.Equal("paid", update.Status);
        }

        [Fact]
        public async Task RecordVendorPayment_UnpaidBillWithPartAmount_BecomesPartial()
        {
            _bills.Bill = OpenBill(40000m, 0m, "unpaid");

            await _service.RecordVendorPayment(7, Payment(10000m));

            var update = Assert.Single(_bills.UpdateCalls);
            Assert.Equal(10000m, update.Paid);
            Assert.Equal(30000m, update.Balance);
            Assert.Equal("partial", update.Status);
        }

        [Fact]
        public async Task RecordVendorPayment_UnpaidBillPaidInOneGo_BecomesPaid()
        {
            _bills.Bill = OpenBill(40000m, 0m, "unpaid");

            await _service.RecordVendorPayment(7, Payment(40000m));

            var update = Assert.Single(_bills.UpdateCalls);
            Assert.Equal(40000m, update.Paid);
            Assert.Equal(0m, update.Balance);
            Assert.Equal("paid", update.Status);
        }

        [Fact]
        public async Task RecordVendorPayment_OneCentBelowTheBalance_StaysPartial()
        {
            await _service.RecordVendorPayment(7, Payment(24999.99m));

            var update = Assert.Single(_bills.UpdateCalls);
            Assert.Equal(39999.99m, update.Paid);
            Assert.Equal(0.01m, update.Balance);
            Assert.Equal("partial", update.Status);
        }

        [Fact]
        public async Task RecordVendorPayment_NeverChangesTheBillAmount()
        {
            await _service.RecordVendorPayment(7, Payment(10000m));

            Assert.Equal(40000m, _bills.UpdateCalls[0].Amount);
        }

        [Fact]
        public async Task RecordVendorPayment_BillUpdateAffectsNoRows_ThrowsRollsBackAndDoesNotCommit()
        {
            _bills.UpdateRows = 0;

            await Assert.ThrowsAsync<InvalidOperationException>(() => _service.RecordVendorPayment(7, Payment()));

            Assert.Equal(1, _connection.Transaction!.Rollbacks);
            Assert.DoesNotContain("commit", _log);
            Assert.Empty(_vendors.UpdateCalls);
        }

        // ---- ValidatePaymentAmount (AC 1, 2) ----

        [Theory]
        [InlineData(0)]
        [InlineData(-5)]
        public async Task RecordVendorPayment_AmountNotPositive_ThrowsValidationAndSavesNothing(double amount)
        {
            var ex = await Assert.ThrowsAsync<ValidationException>(
                () => _service.RecordVendorPayment(7, Payment((decimal)amount)));

            Assert.Contains("greater than zero", ex.Message);
            AssertNothingSaved();
            Assert.Equal(1, _connection.Transaction!.Rollbacks);
        }

        [Fact]
        public async Task RecordVendorPayment_AmountAboveBalance_ThrowsValidationAndSavesNothing()
        {
            var ex = await Assert.ThrowsAsync<ValidationException>(
                () => _service.RecordVendorPayment(7, Payment(25000.01m)));

            Assert.Contains("cannot exceed the AP balance", ex.Message);
            AssertNothingSaved();
            Assert.Empty(_accounts.GetByIdCalls);
        }

        [Fact]
        public async Task RecordVendorPayment_AmountEqualToBalance_IsAccepted()
        {
            var result = await _service.RecordVendorPayment(7, Payment(25000m));

            Assert.NotNull(result);
            Assert.Single(_payments.InsertCalls);
        }

        [Fact]
        public async Task RecordVendorPayment_AmountOneCentAboveBalanceAfterEarlierInstallments_IsRejected()
        {
            _bills.Bill = OpenBill(40000m, 39000m);

            await Assert.ThrowsAsync<ValidationException>(() => _service.RecordVendorPayment(7, Payment(1000.01m)));

            AssertNothingSaved();
        }

        // ---- EnsureBillOpen ----

        [Fact]
        public async Task RecordVendorPayment_PaidBill_ThrowsValidationAndSavesNothing()
        {
            _bills.Bill = OpenBill(40000m, 40000m, "paid");

            var ex = await Assert.ThrowsAsync<ValidationException>(() => _service.RecordVendorPayment(7, Payment(1m)));

            Assert.Contains("already paid", ex.Message);
            AssertNothingSaved();
            Assert.Empty(_accounts.GetByIdCalls);
        }

        [Fact]
        public async Task RecordVendorPayment_ZeroBalanceBillWithOpenStatus_ThrowsValidation()
        {
            _bills.Bill = OpenBill(40000m, 40000m, "partial");

            await Assert.ThrowsAsync<ValidationException>(() => _service.RecordVendorPayment(7, Payment(1m)));

            AssertNothingSaved();
        }

        [Fact]
        public async Task RecordVendorPayment_UnknownStatus_ThrowsValidation()
        {
            _bills.Bill = OpenBill(40000m, 15000m, "cancelled");

            await Assert.ThrowsAsync<ValidationException>(() => _service.RecordVendorPayment(7, Payment()));

            AssertNothingSaved();
        }

        [Theory]
        [InlineData("unpaid", 0)]
        [InlineData("partial", 15000)]
        public async Task RecordVendorPayment_UnpaidOrPartialBill_IsOpen(string status, double paid)
        {
            _bills.Bill = OpenBill(40000m, (decimal)paid, status);

            var result = await _service.RecordVendorPayment(7, Payment(1000m));

            Assert.NotNull(result);
            Assert.Single(_payments.InsertCalls);
        }

        // ---- ValidateBankAccount (AC 6) ----

        [Fact]
        public async Task RecordVendorPayment_UnknownBankAccount_ThrowsValidationAndSavesNothing()
        {
            _accounts.Account = null;

            var ex = await Assert.ThrowsAsync<ValidationException>(() => _service.RecordVendorPayment(7, Payment()));

            Assert.Contains("existing bank account", ex.Message);
            AssertNothingSaved();
        }

        [Fact]
        public async Task RecordVendorPayment_InactiveBankAccount_ThrowsValidationAndSavesNothing()
        {
            _accounts.Account = new BankAccountResponseDto { Id = 1, BookBalance = 50000m, Status = Status.Inactive };

            var ex = await Assert.ThrowsAsync<ValidationException>(() => _service.RecordVendorPayment(7, Payment()));

            Assert.Contains("must be active", ex.Message);
            AssertNothingSaved();
        }

        [Fact]
        public async Task RecordVendorPayment_AccountWithoutStatus_ThrowsValidation()
        {
            _accounts.Account = new BankAccountResponseDto { Id = 1, BookBalance = 50000m, Status = null };

            await Assert.ThrowsAsync<ValidationException>(() => _service.RecordVendorPayment(7, Payment()));

            AssertNothingSaved();
        }

        [Fact]
        public async Task RecordVendorPayment_AmountAboveBookBalance_ThrowsValidationAndSavesNothing()
        {
            _accounts.Account = new BankAccountResponseDto { Id = 1, BookBalance = 9999.99m, Status = Status.Active };

            var ex = await Assert.ThrowsAsync<ValidationException>(() => _service.RecordVendorPayment(7, Payment(10000m)));

            Assert.Contains("book balance", ex.Message);
            AssertNothingSaved();
        }

        [Fact]
        public async Task RecordVendorPayment_AmountEqualToBookBalance_IsAccepted()
        {
            _accounts.Account = new BankAccountResponseDto { Id = 1, BookBalance = 10000m, Status = Status.Active };

            var result = await _service.RecordVendorPayment(7, Payment(10000m));

            Assert.NotNull(result);
            Assert.Single(_posting.Calls);
        }

        // ---- Payment method and reference ----

        [Theory]
        [InlineData("Cash", "Cash")]
        [InlineData("check", "Check")]
        [InlineData("CARD", "Card")]
        [InlineData(" transfer ", "Transfer")]
        public async Task RecordVendorPayment_AllowedMethod_IsStoredWithItsCanonicalName(string method, string expected)
        {
            await _service.RecordVendorPayment(7, Payment(method: method));

            Assert.Equal(expected, _payments.InsertCalls[0].Payment.PaymentMethod);
        }

        [Theory]
        [InlineData("bitcoin")]
        [InlineData("")]
        [InlineData("7")]
        public async Task RecordVendorPayment_UnknownMethod_ThrowsValidationBeforeOpeningAConnection(string method)
        {
            var ex = await Assert.ThrowsAsync<ValidationException>(() => _service.RecordVendorPayment(7, Payment(method: method)));

            Assert.Contains("Payment method", ex.Message);
            Assert.DoesNotContain("open", _log);
            Assert.DoesNotContain("begin", _log);
            AssertNothingSaved();
        }

        [Fact]
        public async Task RecordVendorPayment_ReferenceOver100Characters_ThrowsValidationAndSavesNothing()
        {
            await Assert.ThrowsAsync<ValidationException>(
                () => _service.RecordVendorPayment(7, Payment(reference: new string('x', 101))));

            AssertNothingSaved();
        }

        [Fact]
        public async Task RecordVendorPayment_ReferenceOf100Characters_IsAccepted()
        {
            var result = await _service.RecordVendorPayment(7, Payment(reference: new string('x', 100)));

            Assert.NotNull(result);
        }

        // ---- RecalculateVendorBalance (AC 5) ----

        [Fact]
        public async Task RecordVendorPayment_SetsVendorBalanceToTheOpenBalanceOfTheBillsVendor()
        {
            _bills.OpenBalance = 17500m;

            await _service.RecordVendorPayment(7, Payment());

            var read = Assert.Single(_bills.OpenBalanceCalls);
            Assert.Equal(3, read.VendorId);
            var write = Assert.Single(_vendors.UpdateCalls);
            Assert.Equal(3, write.VendorId);
            Assert.Equal(17500m, write.Balance);
        }

        [Fact]
        public async Task RecordVendorPayment_VendorBalanceIsReadAfterTheBillIsUpdated()
        {
            await _service.RecordVendorPayment(7, Payment());

            Assert.True(_log.IndexOf("update-bill") < _log.IndexOf("get-open-balance"));
            Assert.True(_log.IndexOf("get-open-balance") < _log.IndexOf("update-vendor"));
        }

        [Fact]
        public async Task RecordVendorPayment_WhenNothingIsOwedAnymore_WritesZeroVendorBalance()
        {
            _bills.OpenBalance = 0m;

            await _service.RecordVendorPayment(7, Payment(25000m));

            Assert.Equal(0m, _vendors.UpdateCalls[0].Balance);
        }

        // ---- Failures roll everything back ----

        [Fact]
        public async Task RecordVendorPayment_PostingFails_RollsBackRethrowsAndWritesNothingElse()
        {
            _posting.Throw = new InvalidOperationException("boom");

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _service.RecordVendorPayment(7, Payment()));

            Assert.Equal("boom", ex.Message);
            Assert.Equal(1, _connection.Transaction!.Rollbacks);
            Assert.Empty(_payments.InsertCalls);
            Assert.Empty(_bills.UpdateCalls);
            Assert.Empty(_vendors.UpdateCalls);
            Assert.DoesNotContain("commit", _log);
        }

        [Fact]
        public async Task RecordVendorPayment_InsertFails_RollsBackAndLeavesBillAndVendorUntouched()
        {
            _payments.Throw = new InvalidOperationException("insert failed");

            await Assert.ThrowsAsync<InvalidOperationException>(() => _service.RecordVendorPayment(7, Payment()));

            Assert.Equal(1, _connection.Transaction!.Rollbacks);
            Assert.Empty(_bills.UpdateCalls);
            Assert.Empty(_vendors.UpdateCalls);
            Assert.DoesNotContain("commit", _log);
        }

        // ---- Response ----

        [Fact]
        public async Task RecordVendorPayment_ReturnsUpdatedBillWithVendorBalanceAndPayments()
        {
            _vendors.Vendor = new VendorResponseDto { Id = 3, Name = "Acme Motors", Balance = 15000m };
            _payments.History = new List<BankPayment>
            {
                new() { Id = 1, PaymentNumber = "PAY-2026-000001", PaymentDate = new DateTime(2026, 10, 1), PaymentMethod = "Check",
                        Reference = "CHK-1", Amount = 15000m, BankAccountId = 1, TransactionId = 100, Status = "Posted" },
                new() { Id = 88, PaymentNumber = "PAY-2026-000088", PaymentDate = new DateTime(2026, 11, 8), PaymentMethod = "Transfer",
                        Reference = "WIRE-9", Amount = 10000m, BankAccountId = 1, TransactionId = 555, Status = "Posted" }
            };

            var result = await _service.RecordVendorPayment(7, Payment(10000m));

            Assert.NotNull(result);
            Assert.Equal(7, result!.Id);
            Assert.Equal("AP-2026-000007", result.Number);
            Assert.Equal(3, result.Vendor_Id);
            Assert.Equal(9, result.Vehicle_Id);
            Assert.Equal(new DateTime(2026, 10, 1), result.Date);
            Assert.Equal(new DateTime(2026, 12, 1), result.Due_Date);
            Assert.Equal(40000m, result.Amount);
            Assert.Equal(25000m, result.Paid);
            Assert.Equal(15000m, result.Balance);
            Assert.Equal("partial", result.Status);
            Assert.Equal(15000m, result.Vendor_Balance);
            Assert.Equal(new long[] { 3 }, _vendors.GetByIdCalls);
            Assert.Equal(new long[] { 7 }, _payments.HistoryCalls);

            Assert.Equal(2, result.Payments.Count);
            var last = result.Payments[1];
            Assert.Equal(88, last.Id);
            Assert.Equal("PAY-2026-000088", last.Payment_Number);
            Assert.Equal(new DateTime(2026, 11, 8), last.Payment_Date);
            Assert.Equal("Transfer", last.Payment_Method);
            Assert.Equal("WIRE-9", last.Reference);
            Assert.Equal(10000m, last.Amount);
            Assert.Equal(1, last.Bank_Account_Id);
            Assert.Equal(555, last.Transaction_Id);
            Assert.Equal("Posted", last.Status);
        }

        [Fact]
        public async Task RecordVendorPayment_KeepsThePaymentsInTheOrderTheRepositoryReturnsThem()
        {
            _payments.History = new List<BankPayment>
            {
                new() { Id = 5, PaymentNumber = "PAY-2026-000005", PaymentDate = new DateTime(2026, 11, 1), Amount = 1m },
                new() { Id = 6, PaymentNumber = "PAY-2026-000006", PaymentDate = new DateTime(2026, 11, 2), Amount = 2m },
                new() { Id = 4, PaymentNumber = "PAY-2026-000004", PaymentDate = new DateTime(2026, 11, 3), Amount = 3m }
            };

            var result = await _service.RecordVendorPayment(7, Payment());

            Assert.Equal(new long[] { 5, 6, 4 }, result!.Payments.Select(p => p.Id).ToArray());
        }

        // ---- Public method: GetPaymentHistory ----

        [Fact]
        public async Task GetPaymentHistory_PassesBillIdToRepositoryWithoutATransaction()
        {
            await _service.GetPaymentHistory(7);

            var call = Assert.Single(_bills.GetByIdCalls);
            Assert.Equal(7, call.Id);
            Assert.Null(call.Tx);
            Assert.Equal(new long[] { 7 }, _payments.HistoryCalls);
        }

        [Fact]
        public async Task GetPaymentHistory_UnknownBill_ReturnsNullWithoutLoadingPayments()
        {
            _bills.Bill = null;

            var result = await _service.GetPaymentHistory(999);

            Assert.Null(result);
            Assert.Empty(_payments.HistoryCalls);
            Assert.Empty(_vendors.GetByIdCalls);
        }

        [Fact]
        public async Task GetPaymentHistory_ReturnsBillVendorBalanceAndPayments()
        {
            _vendors.Vendor = new VendorResponseDto { Id = 3, Name = "Acme Motors", Balance = 25000m };
            _payments.History = new List<BankPayment>
            {
                new() { Id = 1, PaymentNumber = "PAY-2026-000001", PaymentMethod = "Check", Reference = "CHK-1",
                        Amount = 15000m, BankAccountId = 1, TransactionId = 100, Status = "Posted" }
            };

            var result = await _service.GetPaymentHistory(7);

            Assert.NotNull(result);
            Assert.Equal(7, result!.Id);
            Assert.Equal(40000m, result.Amount);
            Assert.Equal(15000m, result.Paid);
            Assert.Equal(25000m, result.Balance);
            Assert.Equal("partial", result.Status);
            Assert.Equal(25000m, result.Vendor_Balance);
            var payment = Assert.Single(result.Payments);
            Assert.Equal("PAY-2026-000001", payment.Payment_Number);
            Assert.Equal(15000m, payment.Amount);
            Assert.Equal(100, payment.Transaction_Id);
        }

        [Fact]
        public async Task GetPaymentHistory_BillWithoutPayments_ReturnsEmptyList()
        {
            var result = await _service.GetPaymentHistory(7);

            Assert.NotNull(result);
            Assert.Empty(result!.Payments);
        }

        [Fact]
        public async Task GetPaymentHistory_ChangesNothing()
        {
            await _service.GetPaymentHistory(7);

            Assert.DoesNotContain("open", _log);
            Assert.DoesNotContain("begin", _log);
            AssertNothingSaved();
        }

        // ---- GetAllAp: CalculateDaysOverdue and DetermineAgingStatus (private static, tested through GetAllAp). As-of 2026-11-08. ----

        private static readonly DateTime AsOf = new(2026, 11, 8);

        private static ApAgingDto AgingItem(DateTime? due, decimal balance = 500m, string status = "unpaid") => new()
        {
            Id = 1, Number = "AP-2026-000001", VendorId = 3, Vendor_Name = "Acme Motors",
            Due_Date = due, Amount = 1000m, Paid = 1000m - balance, Balance = balance, Status = status
        };

        private async Task<ApAgingDto> AgeSingleAsync(DateTime? due, decimal balance = 500m, string status = "unpaid", DateTime? asOf = null)
        {
            _bills.Page = new PagedList<ApAgingDto>(new List<ApAgingDto> { AgingItem(due, balance, status) }, 1, 1, 5);
            var result = await _service.GetAllAp(new ApAgingParameters { AsOfDate = asOf ?? AsOf });
            return Assert.Single(result);
        }

        [Fact]
        public async Task GetAllAp_DueTenDaysBeforeAsOf_DaysOverdueIs10()
        {
            var item = await AgeSingleAsync(new DateTime(2026, 10, 29));

            Assert.Equal(10, item.Days_Overdue);
        }

        [Fact]
        public async Task GetAllAp_DueOnAsOfDate_DaysOverdueIsZero()
        {
            var item = await AgeSingleAsync(new DateTime(2026, 11, 8));

            Assert.Equal(0, item.Days_Overdue);
        }

        [Fact]
        public async Task GetAllAp_DueAfterAsOf_DaysOverdueNeverBelowZero()
        {
            var item = await AgeSingleAsync(new DateTime(2026, 12, 1));

            Assert.Equal(0, item.Days_Overdue);
        }

        [Fact]
        public async Task GetAllAp_NoDueDate_DaysOverdueIsZero()
        {
            var item = await AgeSingleAsync(null);

            Assert.Equal(0, item.Days_Overdue);
        }

        [Fact]
        public async Task GetAllAp_DueDateWithTimeOfDay_CountsWholeDays()
        {
            var item = await AgeSingleAsync(new DateTime(2026, 11, 7, 23, 59, 0), asOf: new DateTime(2026, 11, 8, 0, 1, 0));

            Assert.Equal(1, item.Days_Overdue);
        }

        [Fact]
        public async Task GetAllAp_OpenBillPastDue_StatusIsOverdue()
        {
            var item = await AgeSingleAsync(new DateTime(2026, 10, 1), balance: 600m, status: "partial");

            Assert.Equal("Overdue", item.Aging_Status);
        }

        [Fact]
        public async Task GetAllAp_OpenBillNotYetDue_KeepsStoredStatus()
        {
            var item = await AgeSingleAsync(new DateTime(2026, 12, 1), status: "unpaid");

            Assert.Equal("unpaid", item.Aging_Status);
        }

        [Fact]
        public async Task GetAllAp_NoDueDate_KeepsStoredStatus()
        {
            var item = await AgeSingleAsync(null, status: "partial");

            Assert.Equal("partial", item.Aging_Status);
        }

        [Fact]
        public async Task GetAllAp_PaidBillPastDue_IsNotOverdue()
        {
            var item = await AgeSingleAsync(new DateTime(2026, 9, 1), balance: 0m, status: "paid");

            Assert.Equal("paid", item.Aging_Status);
            Assert.Equal(68, item.Days_Overdue);
        }

        [Fact]
        public async Task GetAllAp_NoAsOfDate_DefaultsToToday()
        {
            var parameters = new ApAgingParameters();

            await _service.GetAllAp(parameters);

            Assert.Equal(DateTime.Today, parameters.AsOfDate);
        }

        [Fact]
        public async Task GetAllAp_PassesParametersToRepository()
        {
            var parameters = new ApAgingParameters { AsOfDate = AsOf, Status = "Overdue", VendorId = 3, PageNumber = 2, PageSize = 10 };

            await _service.GetAllAp(parameters);

            Assert.Same(parameters, Assert.Single(_bills.AllApCalls));
        }

        [Fact]
        public async Task GetAllAp_ReturnsTheSamePageWithMetaDataIntact()
        {
            var page = new PagedList<ApAgingDto>(
                new List<ApAgingDto> { AgingItem(new DateTime(2026, 10, 1)), AgingItem(new DateTime(2026, 12, 1)) }, 12, 2, 2);
            _bills.Page = page;

            var result = await _service.GetAllAp(new ApAgingParameters { AsOfDate = AsOf, PageNumber = 2, PageSize = 2 });

            Assert.Same(page, result);
            Assert.Equal(12, result.MetaData.TotalCount);
            Assert.Equal(2, result.MetaData.CurrentPage);
            Assert.Equal(2, result.Count);
        }

        [Fact]
        public async Task GetAllAp_EmptyPage_ReturnsEmpty()
        {
            var result = await _service.GetAllAp(new ApAgingParameters { AsOfDate = AsOf });

            Assert.Empty(result);
        }

        [Fact]
        public async Task GetAllAp_OnlyReadsTheBills()
        {
            await _service.GetAllAp(new ApAgingParameters { AsOfDate = AsOf });

            Assert.Equal(new[] { "get-all-ap" }, _log);
        }
    }
}
