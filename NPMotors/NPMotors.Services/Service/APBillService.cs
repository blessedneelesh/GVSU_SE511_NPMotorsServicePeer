using System.ComponentModel.DataAnnotations;
using System.Data;
using NPMotors.Repository.Interface;
using NPMotors.Repository.Models;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.Models.Enums;
using NPMotors.Repository.RequestFeatures;
using NPMotors.Services.Abstractions.ServiceInterfaces;

namespace NPMotors.Services.Service
{
    public class APBillService : IAPBillService
    {
        private readonly IAPBillRepository _apBillRepository;
        private readonly IBankPaymentRepository _bankPaymentRepository;
        private readonly IBankAccountRepository _bankAccountRepository;
        private readonly IVendorRepository _vendorRepository;
        private readonly ITransactionPostingService _transactionPostingService;
        private readonly IRepositoryContext _context;

        private const string PaymentStatus = "Posted";
        private const string Unpaid = "unpaid";
        private const string Partial = "partial";
        private const string Paid = "paid";
        private const int MaxReferenceLength = 100;
        private const string OverdueStatus = "Overdue";

        public APBillService(
            IAPBillRepository apBillRepository,
            IBankPaymentRepository bankPaymentRepository,
            IBankAccountRepository bankAccountRepository,
            IVendorRepository vendorRepository,
            ITransactionPostingService transactionPostingService,
            IRepositoryContext context)
        {
            _apBillRepository = apBillRepository;
            _bankPaymentRepository = bankPaymentRepository;
            _bankAccountRepository = bankAccountRepository;
            _vendorRepository = vendorRepository;
            _transactionPostingService = transactionPostingService;
            _context = context;
        }

        // Everything is saved in one database transaction: the Out transaction (book balance and statement line),
        // a new bank payment, the AP bill and the vendor balance. Any failure rolls all of it back.
        public async Task<ApBillResponseDto?> RecordVendorPayment(long apBillId, VendorPaymentCreateDto payment)
        {
            var method = ParsePaymentMethod(payment);

            using IDbConnection connection = _context.GetDatabaseConnection();
            if (connection.State != ConnectionState.Open)
                connection.Open();
            using IDbTransaction tx = connection.BeginTransaction();

            try
            {
                // Locks the bill row, so two payments on the same bill cannot both pass the balance check.
                var bill = await _apBillRepository.GetApBillById(apBillId, tx);
                if (bill is null)
                {
                    tx.Rollback();
                    return null;
                }

                EnsureBillOpen(bill);
                ValidatePaymentAmount(payment.Amount, bill);

                var account = await _bankAccountRepository.GetBankAccountById(payment.Bank_Account_Id);
                ValidateBankAccount(account, payment.Amount);

                var transactionId = await _transactionPostingService.PostTransaction(new PostTransactionRequest
                {
                    BankAccountId = payment.Bank_Account_Id,
                    Direction = Direction.Out,
                    Amount = payment.Amount,
                    Date = payment.Payment_Date,
                    Type = TransactionType.BankPayment,
                    SourceNumber = bill.Number,
                    Description = $"Payment for AP bill {bill.Number}",
                    Status = TransactionStatus.Posted
                }, tx);

                // Always a new row: earlier payments are never updated.
                await _bankPaymentRepository.InsertBankPayment(new BankPayment
                {
                    TransactionId = transactionId,
                    BankAccountId = payment.Bank_Account_Id,
                    VendorId = bill.VendorId,
                    ApBillId = bill.Id,
                    VehicleId = bill.VehicleId,
                    PaymentDate = payment.Payment_Date,
                    PaymentMethod = method,
                    Reference = payment.Reference?.Trim(),
                    Amount = payment.Amount,
                    Status = PaymentStatus
                }, tx);

                ApplyPayment(bill, payment.Amount);
                var rows = await _apBillRepository.UpdateApBillPayment(bill, tx);
                if (rows == 0)
                    throw new InvalidOperationException("The AP bill could not be updated.");

                await RecalculateVendorBalance(bill.VendorId, tx);

                tx.Commit();
            }
            catch
            {
                if (tx.Connection is not null)
                    tx.Rollback();
                throw;
            }

            return await BuildResponse(apBillId);
        }

        public async Task<ApBillResponseDto?> GetPaymentHistory(long apBillId)
        {
            return await BuildResponse(apBillId);
        }

        public async Task<PagedList<ApBillResponseDto>> GetAllApBills(ApBillParameters parameters)
        {
            ValidateApBillParameters(parameters);

            var asOfDate = parameters.AsOfDate ??= DateTime.Today;

            var page = await _apBillRepository.GetAllApBills(parameters);
            foreach (var item in page)
            {
                item.Days_Overdue = CalculateDaysOverdue(item.Due_Date, asOfDate);
                item.Status = DetermineAgingStatus(item.Balance, item.Status, item.Days_Overdue);
            }
            return page;
        }

        // PageNumber and PageSize >= 1, VendorId > 0 when given, Status "unpaid" | "partial" | "overdue" | "paid" or empty.
        private static void ValidateApBillParameters(ApBillParameters parameters)
        {
            if (parameters.PageNumber < 1 || parameters.PageSize < 1)
                throw new ValidationException("PageNumber and PageSize must be at least 1.");
            if (parameters.VendorId is <= 0)
                throw new ValidationException("VendorId must be greater than zero.");
            var status = parameters.Status?.Trim().ToLowerInvariant();
            if (!string.IsNullOrEmpty(status) && status != Unpaid && status != Partial && status != Paid && status != "overdue")
                throw new ValidationException("Status must be unpaid, partial, overdue or paid.");
        }

        public async Task<ApDetailDto?> GetApById(long id)
        {
            var ap = await _apBillRepository.GetApById(id);
            if (ap is not null)
                ap.Days = CalculateDaysOverdue(ap.DueDate, DateTime.Today);
            return ap;
        }

        // max(0, (asOfDate - dueDate).Days); 0 when dueDate is null.
        private static int CalculateDaysOverdue(DateTime? dueDate, DateTime asOfDate)
        {
            if (dueDate is null)
                return 0;
            return Math.Max(0, (asOfDate.Date - dueDate.Value.Date).Days);
        }

        // "Overdue" when balance > 0 and daysOverdue > 0, otherwise the stored status.
        private static string DetermineAgingStatus(decimal balance, string status, int daysOverdue)
        {
            return balance > 0 && daysOverdue > 0 ? OverdueStatus : status;
        }

        // Only "unpaid" or "partial" bills (Balance > 0) accept a payment.
        private void EnsureBillOpen(APBill bill)
        {
            if (bill.Balance <= 0 || (bill.Status != Unpaid && bill.Status != Partial))
                throw new ValidationException("The AP bill is already paid.");
        }

        // Amount > 0 and Amount <= bill.Balance.
        private static void ValidatePaymentAmount(decimal amount, APBill bill)
        {
            if (amount <= 0)
                throw new ValidationException("Amount must be greater than zero.");
            if (amount > bill.Balance)
                throw new ValidationException("Amount cannot exceed the AP balance.");
        }

        // The account exists, is active, and the amount does not exceed its book balance.
        private static void ValidateBankAccount(BankAccountResponseDto? account, decimal amount)
        {
            if (account is null)
                throw new ValidationException("An existing bank account is required.");
            if (account.Status != Status.Active)
                throw new ValidationException("The bank account must be active.");
            if (amount > account.BookBalance)
                throw new ValidationException("Amount cannot exceed the bank account's book balance.");
        }

        // Paid = Paid + Amount; Balance = bill.Amount - Paid; Status from the new paid total.
        private static void ApplyPayment(APBill bill, decimal amount)
        {
            bill.Paid += amount;
            bill.Balance = bill.Amount - bill.Paid;
            bill.Status = DeterminePaymentStatus(bill.Paid, bill.Amount);
        }

        // "paid" when paid = total, "partial" when 0 < paid < total, "unpaid" when paid = 0.
        private static string DeterminePaymentStatus(decimal paid, decimal total)
        {
            if (paid == total)
                return Paid;
            return paid > 0 ? Partial : Unpaid;
        }

        // The vendor balance is the sum of the vendor's open AP bills, read inside the same transaction.
        private async Task RecalculateVendorBalance(long vendorId, IDbTransaction tx)
        {
            var balance = await _apBillRepository.GetOpenBalanceByVendor(vendorId, tx);
            await _vendorRepository.UpdateVendorBalance(vendorId, balance, tx);
        }

        // Cash | Check | Card | Transfer (any casing); returns the stored spelling.
        private static string ParsePaymentMethod(VendorPaymentCreateDto payment)
        {
            if (!Enum.TryParse<PaymentMethod>(payment.Payment_Method?.Trim(), true, out var method) || !Enum.IsDefined(method))
                throw new ValidationException("Payment method must be Cash, Check, Card or Transfer.");
            if (payment.Reference?.Trim().Length > MaxReferenceLength)
                throw new ValidationException($"Reference cannot exceed {MaxReferenceLength} characters.");
            return method.ToString();
        }

        private async Task<ApBillResponseDto?> BuildResponse(long apBillId)
        {
            var bill = await _apBillRepository.GetApBillById(apBillId, null);
            if (bill is null)
                return null;

            var payments = await _bankPaymentRepository.GetPaymentsByBillId(apBillId);
            var vendor = await _vendorRepository.GetVendorById(bill.VendorId);

            return new ApBillResponseDto
            {
                Id = bill.Id,
                Number = bill.Number,
                Vendor_Id = bill.VendorId,
                Vehicle_Id = bill.VehicleId,
                Date = bill.Date,
                Due_Date = bill.DueDate,
                Amount = bill.Amount,
                Paid = bill.Paid,
                Balance = bill.Balance,
                Status = bill.Status,
                Vendor_Balance = vendor?.Balance ?? 0m,
                Payments = payments.Select(p => new VendorPaymentResponseDto
                {
                    Id = p.Id,
                    Payment_Number = p.PaymentNumber,
                    Payment_Date = p.PaymentDate,
                    Payment_Method = p.PaymentMethod,
                    Reference = p.Reference,
                    Amount = p.Amount,
                    Bank_Account_Id = p.BankAccountId,
                    Transaction_Id = p.TransactionId,
                    Status = p.Status
                }).ToList()
            };
        }
    }
}
