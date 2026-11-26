namespace NPMotors.Repository.Models.Dto
{
    public class SourceBalanceDto
    {
        // "ARInvoice" | "SalesInvoice" | "APBill"
        public string Document_Type { get; set; } = string.Empty;
        public long Document_Id { get; set; }
        public decimal Amount { get; set; }
        public decimal Paid { get; set; }
        public decimal Balance { get; set; }
        public string Status { get; set; } = string.Empty;
    }
}
