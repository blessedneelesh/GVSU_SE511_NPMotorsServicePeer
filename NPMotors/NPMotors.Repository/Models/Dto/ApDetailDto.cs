namespace NPMotors.Repository.Models.Dto
{
    public class ApDetailDto
    {
        public long Id { get; set; }
        public string Number { get; set; } = string.Empty;
        public long VendorId { get; set; }
        public string Vendor { get; set; } = string.Empty;
        public long? VehicleId { get; set; }
        public DateTime? Date { get; set; }
        public DateTime? DueDate { get; set; }
        public decimal Amount { get; set; }
        public decimal Paid { get; set; }
        public decimal Balance { get; set; }
        // Calculated by APBillService: days past the due date as of today.
        public int Days { get; set; }
        // Stored status (unpaid / partial / paid).
        public string Status { get; set; } = string.Empty;
        public string? Contact { get; set; }
        public string? Phone { get; set; }
        public string? Email { get; set; }
        public string? Address { get; set; }
    }
}
