namespace NPMotors.Repository.Models.Dto
{
    public class ArDetailDto
    {
        public long Id { get; set; }
        public long InvoiceId { get; set; }
        public string Number { get; set; } = string.Empty;
        public string Customer { get; set; } = string.Empty;
        public DateTime? Date { get; set; }
        public DateTime? DueDate { get; set; }
        public decimal Amount { get; set; }
        public decimal Paid { get; set; }
        public decimal Balance { get; set; }
        // Calculated by ArService: days past the due date as of today.
        public int Days { get; set; }
        // Stored status (unpaid / partial / paid).
        public string Status { get; set; } = string.Empty;
        public decimal? Invoice_Total { get; set; }
        public DateTime? Invoice_Date { get; set; }
        public string? Phone { get; set; }
        public string? Email { get; set; }
        public string? Address { get; set; }
    }
}
