using NPMotors.Repository.Models.Enums;

namespace NPMotors.Repository.Models.Dto
{
    public class ArInvoiceResponseDto
    {
        public long Id { get; set; }
        public string Number { get; set; } = string.Empty;
        public string InvoiceNumber { get; set; } = string.Empty;
        public string CustomerName { get; set; } = string.Empty;
        public DateTime? DueDate { get; set; }
        public decimal Amount { get; set; }
        public decimal Paid { get; set; }
        public decimal Balance { get; set; }
        public PaymentStatus Status { get; set; }
        public List<BankReceiptResponseDto> Payments { get; set; } = new();
    }
}
