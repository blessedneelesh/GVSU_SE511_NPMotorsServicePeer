namespace NPMotors.Repository.Models
{
    // Created only when Amount_Paid > 0. TransactionId comes from PostTransaction, so the Transaction is posted first.
    public class BankPayment
    {
        public long Id { get; set; }
        public long TransactionId { get; set; }
        public long BankAccountId { get; set; }
        public long? VendorId { get; set; }
        // Set only for a Partial purchase.
        public long? ApBillId { get; set; }
        public long? VehicleId { get; set; }
        public string PaymentNumber { get; set; } = string.Empty;
        public DateTime PaymentDate { get; set; }
        public string PaymentMethod { get; set; } = string.Empty;
        public string? Reference { get; set; }
        public decimal Amount { get; set; }
        // Story 21: set for a payment not linked to AP (ApBillId and VehicleId stay null).
        public string? Payee { get; set; }
        public string? PaymentCategory { get; set; }
        public string? Description { get; set; }
        public string Status { get; set; } = string.Empty;
    }
}
