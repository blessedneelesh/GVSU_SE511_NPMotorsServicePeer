namespace NPMotors.Repository.Models.Dto
{
    public class SalesInvoiceByCustomerDto
    {
        public string Invoice_Number { get; set; } = string.Empty;
        public DateTime Invoice_Date { get; set; }
        // Year, make and model of the vehicle sold.
        public string Vehicle { get; set; } = string.Empty;
        public string Salesperson { get; set; } = string.Empty;
        public decimal Amount { get; set; }
        public decimal Paid { get; set; }
        // Null while the invoice is a Draft.
        public string? Payment_Status { get; set; }
    }
}
