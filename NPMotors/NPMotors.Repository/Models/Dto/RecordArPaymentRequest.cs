using NPMotors.Repository.Models.Enums;

namespace NPMotors.Repository.Models.Dto
{
    public class RecordArPaymentRequest
    {
        public DateTime ReceiptDate { get; set; }
        public decimal Amount { get; set; }
        public PaymentMethod PaymentMethod { get; set; }
        public string? Reference { get; set; }
        public long BankAccountId { get; set; }
    }
}
