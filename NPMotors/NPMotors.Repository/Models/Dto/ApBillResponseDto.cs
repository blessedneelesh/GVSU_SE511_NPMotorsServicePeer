namespace NPMotors.Repository.Models.Dto
{
    public class ApBillResponseDto
    {
        public long Id { get; set; }
        public string Number { get; set; } = string.Empty;
        public long Vendor_Id { get; set; }
        public long? Vehicle_Id { get; set; }
        public DateTime Date { get; set; }
        public DateTime? Due_Date { get; set; }
        public decimal Amount { get; set; }
        public decimal Paid { get; set; }
        public decimal Balance { get; set; }
        // "unpaid" | "partial" | "paid"
        public string Status { get; set; } = string.Empty;
        // {readOnly}: the vendor's outstanding balance after the payment, set by the service.
        public decimal Vendor_Balance { get; set; }
        // Ordered by Payment_Date, then Payment_Number.
        public List<VendorPaymentResponseDto> Payments { get; set; } = new();
    }
}
