namespace NPMotors.Repository.Models.Dto
{
    public class ApAgingDto
    {
        public long Id { get; set; }
        public string Number { get; set; } = string.Empty;
        public long VendorId { get; set; }
        public string Vendor_Name { get; set; } = string.Empty;
        public DateTime? Due_Date { get; set; }
        public decimal Amount { get; set; }
        public decimal Paid { get; set; }
        public decimal Balance { get; set; }
        // Stored status (unpaid / partial / paid).
        public string Status { get; set; } = string.Empty;
        // Calculated by APBillService.
        public int Days_Overdue { get; set; }
        // Calculated by APBillService: what the list shows.
        public string Aging_Status { get; set; } = string.Empty;
    }
}
