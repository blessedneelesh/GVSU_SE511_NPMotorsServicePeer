using NPMotors.Repository.Models.Enums;

namespace NPMotors.Repository.Models
{
    public class Transaction
    {
        public long Id { get; set; }
        public long BankAccountId { get; set; }
        public string Number { get; set; } = string.Empty;
        public TransactionType Type { get; set; }
        public string SourceNumber { get; set; } = string.Empty;
        public Direction Direction { get; set; }
        public DateTime Date { get; set; }
        public string? Party { get; set; }
        public decimal Amount { get; set; }
        public string? Description { get; set; }
        public TransactionStatus Status { get; set; }
        public DateTime CreatedAt { get; set; }
        public string CreatedBy { get; set; } = string.Empty;
    }
}
