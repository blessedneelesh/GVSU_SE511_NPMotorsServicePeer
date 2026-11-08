using NPMotors.Repository.Models.Enums;

namespace NPMotors.Repository.Models
{
    public class BankStatementLine
    {
        public long Id { get; set; }
        public long BankAccountId { get; set; }
        public long? TransactionId { get; set; }
        public DateTime PostedDate { get; set; }
        public string? BankReference { get; set; }
        public string? Description { get; set; }
        public string? Party { get; set; }
        public TransactionType Type { get; set; }
        public Direction Direction { get; set; }
        public decimal Deposit { get; set; }
        public decimal Withdrawal { get; set; }
        public decimal RunningBalance { get; set; }
        public StatementStatus StatementStatus { get; set; }
        public MatchStatus MatchStatus { get; set; }
        public StatementSource Source { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}
