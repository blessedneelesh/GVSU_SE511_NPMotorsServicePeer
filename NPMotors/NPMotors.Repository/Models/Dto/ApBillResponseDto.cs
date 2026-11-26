namespace NPMotors.Repository.Models.Dto
{
    public class ApBillResponseDto
    {
        public long Id { get; set; }
        public string Number { get; set; } = string.Empty;
        public long Vendor_Id { get; set; }
        public long? Vehicle_Id { get; set; }
        // Story 22: set by the AP list query.
        public string Vendor_Name { get; set; } = string.Empty;
        public DateTime Date { get; set; }
        public DateTime? Due_Date { get; set; }
        public decimal Amount { get; set; }
        public decimal Paid { get; set; }
        public decimal Balance { get; set; }
        // {readOnly} Story 22: max(0, AsOfDate - Due_Date), set by the service.
        public int Days_Overdue { get; set; }
        // {readOnly} Story 22: "Current" | "1-30" | "31-60" | "61-90" | "Over 90".
        public string Aging_Bucket { get; set; } = string.Empty;
        // "unpaid" | "partial" | "paid"; the AP list shows "Overdue" for open bills past due.
        public string Status { get; set; } = string.Empty;
        // {readOnly}: the vendor's outstanding balance (sum of the vendor's open AP balances).
        public decimal Vendor_Balance { get; set; }
        // Ordered by Payment_Date, then Payment_Number.
        public List<VendorPaymentResponseDto> Payments { get; set; } = new();
    }
}
