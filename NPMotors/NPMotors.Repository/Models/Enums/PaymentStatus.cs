namespace NPMotors.Repository.Models.Enums
{
    // Only set once an invoice is Posted; a Draft or Cancelled invoice has no payment status.
    public enum PaymentStatus
    {
        Paid,
        Partial,
        Unpaid,
        Overdue
    }

    public static class PaymentStatusExtensions
    {
        // The database stores the payment status as lowercase text ('paid' / 'partial' / 'unpaid' / 'overdue').
        public static string ToDbValue(this PaymentStatus status) => status.ToString().ToLowerInvariant();
    }
}
