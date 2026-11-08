using NPMotors.Repository.Models.Enums;

namespace NPMotors.Repository.Models
{
    public class BankReceipt
    {
        public long Id { get; set; }
        public long TransactionId { get; set; }
        public long BankAccountId { get; set; }
        // CustomerId and SalesInvoiceId are null for a receipt not linked to an invoice (Story 16).
        public long? CustomerId { get; set; }
        public long? SalesInvoiceId { get; set; }
        // Set only when the sale left a balance (PartialPayment).
        public long? ArInvoiceId { get; set; }
        public string ReceiptNumber { get; set; } = string.Empty;
        public DateTime ReceiptDate { get; set; }
        public PaymentMethod PaymentMethod { get; set; }
        public string? Reference { get; set; }
        // Set only for receipts not linked to an invoice (Story 16).
        public string? Payer { get; set; }
        public string? Description { get; set; }
        public decimal Amount { get; set; }
        public string Status { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public string CreatedBy { get; set; } = string.Empty;
        // Filled only by GetReceiptsByArInvoiceId (JOIN bank_account); not stored on bank_receipt.
        public string BankAccountName { get; set; } = string.Empty;
    }
}
