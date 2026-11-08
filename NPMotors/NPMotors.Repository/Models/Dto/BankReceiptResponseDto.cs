using NPMotors.Repository.Models.Enums;

namespace NPMotors.Repository.Models.Dto
{
    public class BankReceiptResponseDto
    {
        public long Id { get; set; }
        public string ReceiptNumber { get; set; } = string.Empty;
        public DateTime ReceiptDate { get; set; }
        public decimal Amount { get; set; }
        public PaymentMethod PaymentMethod { get; set; }
        public string? Reference { get; set; }
        public string BankAccountName { get; set; } = string.Empty;
        public long TransactionId { get; set; }
    }
}
