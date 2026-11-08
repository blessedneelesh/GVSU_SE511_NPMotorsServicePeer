using System.ComponentModel.DataAnnotations;
using System.Data;
using NPMotors.Repository.Exceptions;
using NPMotors.Repository.Interface;
using NPMotors.Repository.Models;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.Models.Enums;
using NPMotors.Services.Abstractions.ServiceInterfaces;

namespace NPMotors.Services.Service
{
    public class ReceiptService : IReceiptService
    {
        private readonly IArInvoiceRepository _arInvoiceRepository;
        private readonly ISalesInvoiceRepository _salesInvoiceRepository;
        private readonly IBankAccountRepository _bankAccountRepository;
        private readonly IBankReceiptRepository _bankReceiptRepository;
        private readonly ITransactionPostingService _transactionPostingService;
        private readonly IRepositoryContext _context;
        private readonly ICustomerBalanceService _customerBalanceService;

        // No user context exists yet, so new records are attributed to the system.
        private const string CreatedBy = "system";
        private const string ReceiptStatus = "Posted";
        private const string MiscReceiptSource = "BankReceipt";
        private const int MaxReferenceLength = 100;

        public ReceiptService(
            IArInvoiceRepository arInvoiceRepository,
            ISalesInvoiceRepository salesInvoiceRepository,
            IBankAccountRepository bankAccountRepository,
            IBankReceiptRepository bankReceiptRepository,
            ITransactionPostingService transactionPostingService,
            IRepositoryContext context,
            ICustomerBalanceService customerBalanceService)
        {
            _arInvoiceRepository = arInvoiceRepository;
            _salesInvoiceRepository = salesInvoiceRepository;
            _bankAccountRepository = bankAccountRepository;
            _bankReceiptRepository = bankReceiptRepository;
            _transactionPostingService = transactionPostingService;
            _context = context;
            _customerBalanceService = customerBalanceService;
        }

        // Validate everything first (nothing saved), then save the AR invoice, sales invoice, transaction,
        // statement line, book balance and a new bank receipt in one transaction.
        public async Task<ArInvoiceResponseDto?> RecordArPayment(long arInvoiceId, RecordArPaymentRequest request)
        {
            var ar = await _arInvoiceRepository.GetArInvoiceById(arInvoiceId);
            if (ar is null)
                return null;

            var invoice = await _salesInvoiceRepository.GetInvoiceById(ar.InvoiceId)
                ?? throw new ConflictException("The sales invoice of this AR invoice no longer exists.");

            ValidatePaymentAmount(request, ar.Balance);

            var account = await _bankAccountRepository.GetBankAccountById(request.BankAccountId);
            ValidateBankAccount(account);

            var expectedBalance = ar.Balance;
            ApplyPayment(ar, invoice, request.Amount);

            using IDbConnection connection = _context.GetDatabaseConnection();
            if (connection.State != ConnectionState.Open)
                connection.Open();
            using IDbTransaction tx = connection.BeginTransaction();

            try
            {
                var arRows = await _arInvoiceRepository.UpdateArPayment(ar, expectedBalance, tx);
                if (arRows == 0)
                    throw new ConflictException("The AR invoice was changed by another payment. Reload it and try again.");

                var invoiceRows = await _salesInvoiceRepository.UpdateInvoicePayment(invoice, tx);
                if (invoiceRows == 0)
                    throw new ConflictException("The sales invoice is no longer open for payments.");

                var transactionId = await _transactionPostingService.PostTransaction(new PostTransactionRequest
                {
                    BankAccountId = request.BankAccountId,
                    Direction = Direction.In,
                    Amount = request.Amount,
                    Date = request.ReceiptDate,
                    Type = TransactionType.BankReceipt,
                    SourceNumber = invoice.Number,
                    Description = $"Payment received for AR invoice {ar.Number}",
                    Status = TransactionStatus.Posted
                }, tx);

                // Always a new row: earlier receipts are never updated.
                await _bankReceiptRepository.InsertBankReceipt(new BankReceipt
                {
                    TransactionId = transactionId,
                    BankAccountId = request.BankAccountId,
                    CustomerId = invoice.CustomerId,
                    SalesInvoiceId = invoice.Id,
                    ArInvoiceId = ar.Id,
                    ReceiptDate = request.ReceiptDate,
                    PaymentMethod = request.PaymentMethod,
                    Reference = request.Reference?.Trim(),
                    Amount = request.Amount,
                    Status = ReceiptStatus,
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = CreatedBy
                }, tx);

                await _customerBalanceService.UpdateCustomerTotals(invoice.CustomerId, tx);

                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }

            var receipts = await _bankReceiptRepository.GetReceiptsByArInvoiceId(ar.Id);
            return ToResponseDto(ar, receipts);
        }

        public async Task<ArInvoiceResponseDto?> GetPaymentHistory(long arInvoiceId)
        {
            var ar = await _arInvoiceRepository.GetArInvoiceById(arInvoiceId);
            if (ar is null)
                return null;

            var receipts = await _bankReceiptRepository.GetReceiptsByArInvoiceId(arInvoiceId);
            return ToResponseDto(ar, receipts);
        }

        // Validate first (nothing saved), then save the transaction, statement line, book balance and a new
        // bank receipt in one transaction. No customer, invoice or AR repository is touched.
        public async Task<BankReceiptResponseDto> RecordMiscReceipt(RecordMiscReceiptRequest request)
        {
            var account = await _bankAccountRepository.GetBankAccountById(request.BankAccountId);
            ValidateMiscReceipt(request, account);

            var receipt = new BankReceipt
            {
                BankAccountId = request.BankAccountId,
                // CustomerId, SalesInvoiceId and ArInvoiceId stay null.
                ReceiptDate = request.ReceiptDate,
                PaymentMethod = request.PaymentMethod,
                Reference = request.Reference?.Trim(),
                Payer = request.Payer.Trim(),
                Description = request.Description.Trim(),
                Amount = request.Amount,
                Status = ReceiptStatus,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = CreatedBy
            };

            using IDbConnection connection = _context.GetDatabaseConnection();
            if (connection.State != ConnectionState.Open)
                connection.Open();
            using IDbTransaction tx = connection.BeginTransaction();

            try
            {
                receipt.TransactionId = await _transactionPostingService.PostTransaction(new PostTransactionRequest
                {
                    BankAccountId = receipt.BankAccountId,
                    Direction = Direction.In,
                    Amount = receipt.Amount,
                    Date = receipt.ReceiptDate,
                    Type = TransactionType.BankReceipt,
                    SourceNumber = MiscReceiptSource,
                    Party = receipt.Payer,
                    Description = receipt.Description,
                    Status = TransactionStatus.Posted
                }, tx);

                // Always a new row: earlier receipts are never updated.
                receipt.Id = await _bankReceiptRepository.InsertBankReceipt(receipt, tx);
                // Same format the repository stores: RCT-<year>-<id padded to 6>.
                receipt.ReceiptNumber = $"RCT-{receipt.CreatedAt.Year}-{receipt.Id:D6}";

                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }

            return ToResponseDto(receipt, account!);
        }

        private static void ValidateMiscReceipt(RecordMiscReceiptRequest request, BankAccountResponseDto? account)
        {
            var errors = new List<string>();

            if (string.IsNullOrWhiteSpace(request.Payer))
                errors.Add("Payer is required.");
            if (request.ReceiptDate == default)
                errors.Add("Receipt date is required.");
            if (request.Amount <= 0)
                errors.Add("Amount must be greater than zero.");
            if (!Enum.IsDefined(request.PaymentMethod))
                errors.Add("Payment method must be Cash, Card, Transfer or Check.");
            if (request.BankAccountId <= 0)
                errors.Add("Bank account is required.");
            else if (account is null)
                errors.Add("An existing bank account is required.");
            else if (account.Status != Status.Active)
                errors.Add("The bank account must be active.");
            if (string.IsNullOrWhiteSpace(request.Description))
                errors.Add("Description is required.");
            if (request.Reference?.Trim().Length > MaxReferenceLength)
                errors.Add($"Reference cannot exceed {MaxReferenceLength} characters.");

            if (errors.Count > 0)
                throw new ValidationException(string.Join(" ", errors));
        }

        private static BankReceiptResponseDto ToResponseDto(BankReceipt receipt, BankAccountResponseDto account)
        {
            return new BankReceiptResponseDto
            {
                Id = receipt.Id,
                ReceiptNumber = receipt.ReceiptNumber,
                ReceiptDate = receipt.ReceiptDate,
                Payer = receipt.Payer,
                Amount = receipt.Amount,
                PaymentMethod = receipt.PaymentMethod,
                Reference = receipt.Reference,
                Description = receipt.Description,
                BankAccountName = account.Name ?? string.Empty,
                TransactionId = receipt.TransactionId
            };
        }

        private static void ValidatePaymentAmount(RecordArPaymentRequest request, decimal arBalance)
        {
            var errors = new List<string>();

            if (request.Amount <= 0)
                errors.Add("Amount must be greater than zero.");
            else if (arBalance <= 0)
                errors.Add("The AR invoice has no open balance.");
            else if (request.Amount > arBalance)
                errors.Add("Amount cannot exceed the AR balance.");

            if (!Enum.IsDefined(request.PaymentMethod))
                errors.Add("Payment method must be Cash, Card, Transfer or Check.");

            if (request.Reference?.Trim().Length > MaxReferenceLength)
                errors.Add($"Reference cannot exceed {MaxReferenceLength} characters.");

            if (errors.Count > 0)
                throw new ValidationException(string.Join(" ", errors));
        }

        private static void ValidateBankAccount(BankAccountResponseDto? account)
        {
            if (account is null)
                throw new ValidationException("An existing bank account is required.");
            if (account.Status != Status.Active)
                throw new ValidationException("The bank account must be active.");
        }

        private static void ApplyPayment(ArInvoice ar, SalesInvoice invoice, decimal amount)
        {
            ar.Paid += amount;
            ar.Balance = ar.Amount - ar.Paid;
            ar.Status = DeterminePaymentStatus(ar.Paid, ar.Amount);

            invoice.Paid += amount;
            invoice.Balance = invoice.Total - invoice.Paid;
            invoice.PaymentStatus = DeterminePaymentStatus(invoice.Paid, invoice.Total);
        }

        private static PaymentStatus DeterminePaymentStatus(decimal paid, decimal total)
        {
            if (paid >= total)
                return PaymentStatus.Paid;
            if (paid > 0)
                return PaymentStatus.Partial;
            return PaymentStatus.Unpaid;
        }

        private static ArInvoiceResponseDto ToResponseDto(ArInvoice ar, IEnumerable<BankReceipt> receipts)
        {
            return new ArInvoiceResponseDto
            {
                Id = ar.Id,
                Number = ar.Number,
                InvoiceNumber = ar.InvoiceNumber,
                CustomerName = ar.CustomerName,
                DueDate = ar.DueDate,
                Amount = ar.Amount,
                Paid = ar.Paid,
                Balance = ar.Balance,
                Status = ar.Status,
                Payments = receipts.Select(r => new BankReceiptResponseDto
                {
                    Id = r.Id,
                    ReceiptNumber = r.ReceiptNumber,
                    ReceiptDate = r.ReceiptDate,
                    Amount = r.Amount,
                    PaymentMethod = r.PaymentMethod,
                    Reference = r.Reference,
                    BankAccountName = r.BankAccountName,
                    TransactionId = r.TransactionId
                }).ToList()
            };
        }
    }
}
