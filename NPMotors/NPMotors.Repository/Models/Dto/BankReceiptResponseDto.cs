using NPMotors.Repository.Models.Enums;

namespace NPMotors.Repository.Models.Dto
{
    public class BankReceiptResponseDto
    {
        public long Id { get; set; }
        public string ReceiptNumber { get; set; } = string.Empty;
        public DateTime ReceiptDate { get; set; }
        // Payer and Description are set only for receipts not linked to an invoice (Story 16).
        public string? Payer { get; set; }
        public string? Description { get; set; }
        public decimal Amount { get; set; }
        public PaymentMethod PaymentMethod { get; set; }
        public string? Reference { get; set; }
        public string BankAccountName { get; set; } = string.Empty;
        public long TransactionId { get; set; }
    }
}
