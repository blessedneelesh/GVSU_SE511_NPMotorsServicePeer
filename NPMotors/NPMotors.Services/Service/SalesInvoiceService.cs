using NPMotors.Services.Abstractions.ServiceInterfaces;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Text;
using NPMotors.Repository.Exceptions;
using NPMotors.Repository.Interface;
using NPMotors.Repository.Models;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.Models.Enums;
using NPMotors.Repository.RequestFeatures;

namespace NPMotors.Services.Service
{
    public class SalesInvoiceService : ISalesInvoiceService
    {
        private readonly ISalesInvoiceRepository _salesInvoiceRepository;
        private readonly IVehicleRepository _vehicleRepository;
        private readonly IBankAccountRepository _bankAccountRepository;
        private readonly IArInvoiceRepository _arInvoiceRepository;
        private readonly IBankReceiptRepository _bankReceiptRepository;
        private readonly ITransactionPostingService _transactionPostingService;
        private readonly IRepositoryContext _context;
        private readonly ICustomerBalanceService _customerBalanceService;

        // No user context exists yet, so new records are attributed to the system.
        private const string CreatedBy = "system";
        private const string ReceiptStatus = "Posted";
        private const int MaxReferenceLength = 100;

        public SalesInvoiceService(
            ISalesInvoiceRepository salesInvoiceRepository,
            IVehicleRepository vehicleRepository,
            IBankAccountRepository bankAccountRepository,
            IArInvoiceRepository arInvoiceRepository,
            IBankReceiptRepository bankReceiptRepository,
            ITransactionPostingService transactionPostingService,
            IRepositoryContext context,
            ICustomerBalanceService customerBalanceService)
        {
            _salesInvoiceRepository = salesInvoiceRepository;
            _vehicleRepository = vehicleRepository;
            _bankAccountRepository = bankAccountRepository;
            _arInvoiceRepository = arInvoiceRepository;
            _bankReceiptRepository = bankReceiptRepository;
            _transactionPostingService = transactionPostingService;
            _context = context;
            _customerBalanceService = customerBalanceService;
        }

        public async Task<SalesInvoiceResponseDto> AddSalesInvoice(SalesInvoiceCreateDto invoice)
        {
            ValidateInvoice(invoice);

            var created = await _salesInvoiceRepository.AddSalesInvoice(invoice);
            created.Total = CalculateTotal(created.Sale_Price, created.Tax, created.Fees, created.Discount);
            created.Balance = CalculateBalance(created.Total, created.Paid);
            return created;
        }

        public async Task<SalesInvoiceResponseDto> PostSalesInvoice(SalesInvoiceCreateDto invoice)
        {
            ValidateInvoice(invoice);

            var posted = await _salesInvoiceRepository.PostSalesInvoice(invoice);
            posted.Total = CalculateTotal(posted.Sale_Price, posted.Tax, posted.Fees, posted.Discount);
            posted.Balance = CalculateBalance(posted.Total, posted.Paid);
            return posted;
        }

        public async Task<PagedList<SalesInvoiceResponseDto>> GetAllSalesInvoices(SalesInvoiceParameters parameters)
        {
            var invoices = await _salesInvoiceRepository.GetAllSalesInvoices(parameters);
            foreach (var invoice in invoices)
            {
                invoice.Total = CalculateTotal(invoice.Sale_Price, invoice.Tax, invoice.Fees, invoice.Discount);
                invoice.Balance = CalculateBalance(invoice.Total, invoice.Paid);
            }
            return invoices;
        }

        public async Task<SalesInvoiceResponseDto?> GetSalesInvoiceById(long id)
        {
            var invoice = await _salesInvoiceRepository.GetSalesInvoiceById(id);
            if (invoice is null)
                return null;

            invoice.Total = CalculateTotal(invoice.Sale_Price, invoice.Tax, invoice.Fees, invoice.Discount);
            invoice.Balance = CalculateBalance(invoice.Total, invoice.Paid);
            return invoice;
        }

        public async Task<SalesInvoiceResponseDto> CancelSalesInvoice(long id, SalesInvoiceCancelDto reason)
        {
            var text = reason?.Reason?.Trim();
            if (string.IsNullOrEmpty(text))
                throw new ArgumentException("A cancellation reason is required.");
            if (text.Length > 500)
                throw new ArgumentException("The cancellation reason cannot exceed 500 characters.");

            using IDbConnection connection = _context.GetDatabaseConnection();
            if (connection.State != ConnectionState.Open)
                connection.Open();
            using IDbTransaction tx = connection.BeginTransaction();

            SalesInvoiceResponseDto cancelled;
            try
            {
                cancelled = await _salesInvoiceRepository.CancelSalesInvoice(id, new SalesInvoiceCancelDto { Reason = text }, tx);

                // A cancelled credit sale must leave no open AR, so the customer balance drops with it.
                await _arInvoiceRepository.CloseArInvoice(id, tx);
                await _customerBalanceService.UpdateCustomerTotals(cancelled.Customer_Id, tx);

                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }

            cancelled.Total = CalculateTotal(cancelled.Sale_Price, cancelled.Tax, cancelled.Fees, cancelled.Discount);
            cancelled.Balance = CalculateBalance(cancelled.Total, cancelled.Paid);
            return cancelled;
        }

        // Posts a Draft: validate everything first (nothing saved), then save the invoice, vehicle,
        // AR invoice, transaction, statement line, book balance and bank receipt in one transaction.
        public async Task<SalesInvoiceResponseDto?> PostInvoice(long id, PostInvoiceRequest request)
        {
            var invoice = await _salesInvoiceRepository.GetInvoiceById(id);
            if (invoice is null)
                return null;

            EnsureDraft(invoice);
            ValidateInvoice(invoice);
            ValidateAmountReceived(request, invoice.Total);

            if (request.AmountReceived > 0)
            {
                var account = request.BankAccountId is null
                    ? null
                    : await _bankAccountRepository.GetBankAccountById(request.BankAccountId.Value);
                ValidateBankAccount(account);
            }

            ApplyPaymentCondition(invoice, request.AmountReceived);
            invoice.Lifecycle = InvoiceLifecycle.Posted.ToDbValue();

            using IDbConnection connection = _context.GetDatabaseConnection();
            if (connection.State != ConnectionState.Open)
                connection.Open();
            using IDbTransaction tx = connection.BeginTransaction();

            try
            {
                var updatedRows = await _salesInvoiceRepository.UpdatePostedInvoice(invoice, tx);
                if (updatedRows == 0)
                    throw new ConflictException("The invoice is no longer a draft.");

                var soldRows = await _vehicleRepository.MarkVehicleSold(invoice.VehicleId, invoice.Id, tx);
                if (soldRows == 0)
                    throw new ConflictException("The vehicle is already sold.");

                long? arInvoiceId = null;
                var arInvoice = CreateArInvoiceIfBalance(invoice);
                if (arInvoice is not null)
                    arInvoiceId = await _arInvoiceRepository.InsertArInvoice(arInvoice, tx);

                long? receiptId = null;
                if (request.AmountReceived > 0)
                {
                    var receiptDate = request.ReceiptDate ?? DateTime.UtcNow;
                    var transactionId = await _transactionPostingService.PostTransaction(new PostTransactionRequest
                    {
                        BankAccountId = request.BankAccountId!.Value,
                        Direction = Direction.In,
                        Amount = request.AmountReceived,
                        Date = receiptDate,
                        Type = TransactionType.BankReceipt,
                        SourceNumber = invoice.Number,
                        Description = $"Payment received for sales invoice {invoice.Number}",
                        Status = TransactionStatus.Posted
                    }, tx);

                    receiptId = await _bankReceiptRepository.InsertBankReceipt(new BankReceipt
                    {
                        TransactionId = transactionId,
                        BankAccountId = request.BankAccountId.Value,
                        CustomerId = invoice.CustomerId,
                        SalesInvoiceId = invoice.Id,
                        ArInvoiceId = arInvoiceId,
                        ReceiptDate = receiptDate,
                        PaymentMethod = request.PaymentMethod!.Value,
                        Reference = request.Reference?.Trim(),
                        Amount = request.AmountReceived,
                        Status = ReceiptStatus,
                        CreatedAt = DateTime.UtcNow,
                        CreatedBy = CreatedBy
                    }, tx);
                }

                await _customerBalanceService.UpdateCustomerTotals(invoice.CustomerId, tx);

                tx.Commit();

                return new SalesInvoiceResponseDto
                {
                    Id = invoice.Id,
                    Number = invoice.Number,
                    Customer_Id = invoice.CustomerId,
                    Vehicle_Id = invoice.VehicleId,
                    Due_Date = invoice.DueDate,
                    Total = invoice.Total,
                    Paid = invoice.Paid,
                    Balance = invoice.Balance,
                    Lifecycle = InvoiceLifecycle.Posted,
                    Payment_Status = invoice.PaymentStatus,
                    Ar_Invoice_Id = arInvoiceId,
                    Bank_Receipt_Id = receiptId
                };
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }

        private void EnsureDraft(SalesInvoice invoice)
        {
            if (!string.Equals(invoice.Lifecycle, InvoiceLifecycle.Draft.ToDbValue(), StringComparison.OrdinalIgnoreCase))
                throw new ValidationException("Only a Draft invoice can be posted.");
        }

        // Story 12 rules, run again on the stored invoice.
        private void ValidateInvoice(SalesInvoice invoice)
        {
            var errors = new List<string>();

            if (invoice.CustomerId <= 0)
                errors.Add("Customer is required.");
            if (invoice.VehicleId <= 0)
                errors.Add("Vehicle is required.");
            if (invoice.Total <= 0)
                errors.Add("Invoice total must be greater than zero.");

            if (errors.Count > 0)
                throw new ValidationException(string.Join(" ", errors));
        }

        private static void ValidateAmountReceived(PostInvoiceRequest request, decimal total)
        {
            var errors = new List<string>();
            var amount = request.AmountReceived;

            if (amount < 0)
                errors.Add("Amount received cannot be negative.");
            else if (amount > total)
                errors.Add("Amount received cannot exceed the invoice total.");

            if (!Enum.IsDefined(request.PaymentCondition))
                errors.Add("Payment condition must be PaidInFull, PartialPayment or Credit.");
            else if (amount >= 0 && amount <= total)
            {
                if (request.PaymentCondition == PaymentCondition.PaidInFull && amount != total)
                    errors.Add("A paid-in-full sale must receive the full invoice total.");
                else if (request.PaymentCondition == PaymentCondition.PartialPayment && (amount <= 0 || amount >= total))
                    errors.Add("A partial payment must be greater than zero and less than the invoice total.");
                else if (request.PaymentCondition == PaymentCondition.Credit && amount != 0)
                    errors.Add("A credit sale cannot receive an amount.");
            }

            if (amount > 0)
            {
                if (request.PaymentMethod is null || !Enum.IsDefined(request.PaymentMethod.Value))
                    errors.Add("Payment method must be Cash, Card, Transfer or Check.");
                if (request.Reference?.Trim().Length > MaxReferenceLength)
                    errors.Add($"Reference cannot exceed {MaxReferenceLength} characters.");
            }

            if (errors.Count > 0)
                throw new ValidationException(string.Join(" ", errors));
        }

        private static void ValidateBankAccount(BankAccountResponseDto? account)
        {
            if (account is null)
                throw new ValidationException("An existing bank account is required when an amount is received.");
            if (account.Status != Status.Active)
                throw new ValidationException("The bank account must be active.");
        }

        private static void ApplyPaymentCondition(SalesInvoice invoice, decimal amountReceived)
        {
            invoice.Paid = amountReceived;
            invoice.Balance = invoice.Total - invoice.Paid;
            invoice.PaymentStatus = DeterminePaymentStatus(invoice.Paid, invoice.Total);
        }

        private static PaymentStatus DeterminePaymentStatus(decimal paid, decimal total)
        {
            if (paid == total)
                return PaymentStatus.Paid;
            if (paid > 0)
                return PaymentStatus.Partial;
            return PaymentStatus.Unpaid;
        }

        // Only a sale with a balance gets an AR invoice; its amounts and status come from the Sales Invoice.
        private static ArInvoice? CreateArInvoiceIfBalance(SalesInvoice invoice)
        {
            if (invoice.Balance <= 0)
                return null;

            return new ArInvoice
            {
                InvoiceId = invoice.Id,
                Date = DateTime.UtcNow.Date,
                DueDate = invoice.DueDate,
                Amount = invoice.Total,
                Paid = invoice.Paid,
                Balance = invoice.Balance,
                Status = invoice.PaymentStatus!.Value,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = CreatedBy
            };
        }

        private static decimal CalculateTotal(decimal salePrice, decimal tax, decimal fees, decimal discount)
        {
            return salePrice + tax + fees - discount;
        }

        private static decimal CalculateBalance(decimal total, decimal paid)
        {
            return total - paid;
        }

        private static void ValidateInvoice(SalesInvoiceCreateDto invoice)
        {
            if (invoice.Customer_Id <= 0)
                throw new ArgumentException("Customer is required.");
            if (invoice.Salesperson_Id <= 0)
                throw new ArgumentException("Salesperson is required.");
            if (invoice.Vehicle_Id <= 0)
                throw new ArgumentException("Vehicle is required.");
            if (invoice.Invoice_Date == default)
                throw new ArgumentException("Invoice date is required.");
            if (invoice.Tax < 0)
                throw new ArgumentException("Tax cannot be negative.");
            if (invoice.Fees < 0)
                throw new ArgumentException("Fees cannot be negative.");
            if (invoice.Discount < 0)
                throw new ArgumentException("Discount cannot be negative.");
        }
    }
}
