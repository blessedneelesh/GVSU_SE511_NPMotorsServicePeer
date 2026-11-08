using NPMotors.Repository.Models.Enums;

namespace NPMotors.Repository.Models.Dto
{
    public class PostTransactionRequest
    {
        public long BankAccountId { get; set; }
        public Direction Direction { get; set; }
        public decimal Amount { get; set; }
        public DateTime Date { get; set; }
        public TransactionType Type { get; set; }
        public string SourceNumber { get; set; } = string.Empty;
        public string? Party { get; set; }
        public string? Description { get; set; }
        public TransactionStatus Status { get; set; }
    }
}
