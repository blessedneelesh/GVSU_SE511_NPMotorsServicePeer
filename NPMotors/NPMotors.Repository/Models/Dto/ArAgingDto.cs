namespace NPMotors.Repository.Models.Dto
{
    public class ArAgingDto
    {
        public long Id { get; set; }
        public string Number { get; set; } = string.Empty;
        public long CustomerId { get; set; }
        public string Customer_Name { get; set; } = string.Empty;
        public string Invoice_Number { get; set; } = string.Empty;
        public DateTime? Due_Date { get; set; }
        public decimal Amount { get; set; }
        public decimal Paid { get; set; }
        public decimal Balance { get; set; }
        // Stored status (Unpaid / Partial).
        public string Status { get; set; } = string.Empty;
        // Calculated by ArService.
        public int Days_Overdue { get; set; }
        // Calculated by ArService: what the list shows.
        public string Aging_Status { get; set; } = string.Empty;
    }
}
