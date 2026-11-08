namespace NPMotors.Repository.RequestFeatures
{
    public class TransactionParameters : RequestParameters
    {
        public long? BankAccountId { get; set; }
        // "in" or "out"; empty = all.
        public string? Direction { get; set; }
        public DateTime? DateFrom { get; set; }
        public DateTime? DateTo { get; set; }
    }
}
