namespace NPMotors.Repository.Models.Dto
{
    public class TransactionResponseDto
    {
        public long Id { get; set; }
        public string Number { get; set; } = string.Empty;
        public long Bank_Account_Id { get; set; }
        public string Bank_Account_Name { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string Source_Number { get; set; } = string.Empty;
        public string Direction { get; set; } = string.Empty;
        public DateTime Date { get; set; }
        public string? Party { get; set; }
        public decimal Amount { get; set; }
        public string? Description { get; set; }
        public string Status { get; set; } = string.Empty;
        public DateTime Created_At { get; set; }
        public string Created_By { get; set; } = string.Empty;
    }
}
