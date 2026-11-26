namespace NPMotors.Repository.RequestFeatures
{
    public class ApBillParameters : RequestParameters
    {
        // Empty = all vendors.
        public long? VendorId { get; set; }
        // "unpaid" | "partial" | "overdue" (empty = all open). Paid bills (Balance = 0) are never listed.
        public string? Status { get; set; }
        // Days overdue are counted up to this date; defaults to today in the service.
        public DateTime? AsOfDate { get; set; }
    }
}
