namespace NPMotors.Repository.Models.Enums
{
    public enum InvoiceLifecycle
    {
        Posted,
        Draft,
        Cancelled
    }

    public static class InvoiceLifecycleExtensions
    {
        // The database stores the lifecycle as lowercase text ('draft' / 'posted' / 'cancelled').
        public static string ToDbValue(this InvoiceLifecycle lifecycle) => lifecycle.ToString().ToLowerInvariant();
    }
}
