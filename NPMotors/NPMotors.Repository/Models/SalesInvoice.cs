using NPMotors.Repository.Models.Enums;

namespace NPMotors.Repository.Models
{
    public class SalesInvoice
    {
        public long Id { get; set; }
        public string Number { get; set; } = string.Empty;
        public long CustomerId { get; set; }
        public long VehicleId { get; set; }
        public DateTime? DueDate { get; set; }
        public decimal Total { get; set; }
        public decimal Paid { get; set; }
        public decimal Balance { get; set; }
        // Null while the invoice is a Draft.
        public PaymentStatus? PaymentStatus { get; set; }
        // Stored as lowercase text: 'draft' / 'posted' / 'cancelled'.
        public string Lifecycle { get; set; } = string.Empty;
    }
}
