using NPMotors.Repository.Models.Enums;

namespace NPMotors.Repository.Models.Dto
{
    public class PostInvoiceRequest
    {
        public PaymentCondition PaymentCondition { get; set; }
        // 0 for a Credit sale.
        public decimal AmountReceived { get; set; }
        public PaymentMethod? PaymentMethod { get; set; }
        public string? Reference { get; set; }
        public long? BankAccountId { get; set; }
        public DateTime? ReceiptDate { get; set; }
    }
}
