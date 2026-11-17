using NPMotors.Repository.Models.Enums;

namespace NPMotors.Repository.Models.Dto
{
    // Add Sales Invoice fields plus Post Invoice fields in one flat body.
    public class SalesInvoiceCreateAndPostRequest : SalesInvoiceCreateDto
    {
        public PaymentCondition PaymentCondition { get; set; }
        // 0 for a Credit sale.
        public decimal AmountReceived { get; set; }
        public PaymentMethod? PaymentMethod { get; set; }
        public string? Reference { get; set; }
        public long? BankAccountId { get; set; }
        public DateTime? ReceiptDate { get; set; }

        public PostInvoiceRequest ToPostInvoiceRequest() => new()
        {
            PaymentCondition = PaymentCondition,
            AmountReceived = AmountReceived,
            PaymentMethod = PaymentMethod,
            Reference = Reference,
            BankAccountId = BankAccountId,
            ReceiptDate = ReceiptDate
        };
    }
}
