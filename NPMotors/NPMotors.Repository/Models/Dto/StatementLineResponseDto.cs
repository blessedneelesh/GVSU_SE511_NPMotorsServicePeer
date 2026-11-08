namespace NPMotors.Repository.Models.Dto
{
    public class StatementLineResponseDto
    {
        public long Id { get; set; }
        public long Bank_Account_Id { get; set; }
        public string Bank_Account_Name { get; set; } = string.Empty;
        public long? Transaction_Id { get; set; }
        public DateTime Posted_Date { get; set; }
        public string? Bank_Reference { get; set; }
        public string? Description { get; set; }
        public string? Party { get; set; }
        public string Type { get; set; } = string.Empty;
        public string Direction { get; set; } = string.Empty;
        public decimal Deposit { get; set; }
        public decimal Withdrawal { get; set; }
        public decimal Running_Balance { get; set; }
        public string Statement_Status { get; set; } = string.Empty;
        public string Match_Status { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
    }
}
