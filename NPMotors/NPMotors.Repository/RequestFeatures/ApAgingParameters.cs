namespace NPMotors.Repository.RequestFeatures
{
    public class ApAgingParameters : RequestParameters
    {
        // Days overdue are counted up to this date; defaults to today in the service.
        public DateTime? AsOfDate { get; set; }
        // "Unpaid" | "Partial" | "Overdue" | "Paid" (empty = all AP bills, paid included).
        public string? Status { get; set; }
        // Empty = all vendors.
        public long? VendorId { get; set; }
    }
}
