namespace NPMotors.Repository.RequestFeatures
{
    public class StatementLineParameters : RequestParameters
    {
        public long? BankAccountId { get; set; }
        // "yyyy-MM"; empty = all months.
        public string? Month { get; set; }
        // A transaction type such as "BankReceipt"; empty = all types.
        public string? Type { get; set; }
    }
}
