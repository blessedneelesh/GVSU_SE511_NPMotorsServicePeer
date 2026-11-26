namespace NPMotors.Repository.Models
{
    // Built by VehicleService.CreateApBillIfBalance, only for Partial and Credit purchases.
    public class APBill
    {
        public long Id { get; set; }
        public string Number { get; set; } = string.Empty;
        public long VendorId { get; set; }
        // One AP Bill per vehicle (unique in the database).
        public long? VehicleId { get; set; }
        public DateTime Date { get; set; }
        public DateTime? DueDate { get; set; }
        public decimal Amount { get; set; }
        public decimal Paid { get; set; }
        public decimal Balance { get; set; }
        // "unpaid" | "partial" | "paid"
        public string Status { get; set; } = string.Empty;
    }
}
