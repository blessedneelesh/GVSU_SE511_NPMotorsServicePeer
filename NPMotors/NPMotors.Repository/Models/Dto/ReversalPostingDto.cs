namespace NPMotors.Repository.Models.Dto
{
    public class ReversalPostingDto
    {
        public long Original_Transaction_Id { get; set; }
        public string Reason { get; set; } = string.Empty;
        public string Direction { get; set; } = string.Empty;
        public decimal Amount { get; set; }
        public long Bank_Account_Id { get; set; }
        public string Source_Type { get; set; } = string.Empty;
        public long Source_Id { get; set; }
        public string Source_Number { get; set; } = string.Empty;
        public List<SourceBalanceDto> Restored_Documents { get; set; } = new();
    }
}
