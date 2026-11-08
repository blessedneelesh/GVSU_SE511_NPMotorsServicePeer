namespace NPMotors.Repository.RequestFeatures
{
    public class ArAgingParameters : RequestParameters
    {
        // Days overdue are counted up to this date; defaults to today in the service.
        public DateTime? AsOfDate { get; set; }
        // "Unpaid" | "Partial" | "Overdue" (empty = all open AR).
        public string? Status { get; set; }
        // Empty = all customers.
        public long? CustomerId { get; set; }
    }
}
