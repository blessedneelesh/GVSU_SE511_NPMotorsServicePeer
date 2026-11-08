namespace NPMotors.Repository.Models.Enums
{
    public enum TransactionStatus
    {
        Draft,
        Posted
    }

    public static class TransactionStatusExtensions
    {
        // The database stores the status as lowercase text ('draft' / 'posted').
        public static string ToDbValue(this TransactionStatus status) => status.ToString().ToLowerInvariant();
    }
}
