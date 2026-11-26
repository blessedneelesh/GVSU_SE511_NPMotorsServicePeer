namespace NPMotors.Repository.Models.Dto
{
    public class ReversalSourceDto
    {
        public long Transaction_Id { get; set; }
        public string Type { get; set; } = string.Empty;
        public string Direction { get; set; } = string.Empty;
        public decimal Amount { get; set; }
        public string Status { get; set; } = string.Empty;
        public long Bank_Account_Id { get; set; }
        public long? Reverses_Transaction_Id { get; set; }
        public long? Reversed_By_Transaction_Id { get; set; }
        // "Receipt" | "Payment"; empty when no bank receipt or bank payment belongs to the transaction.
        public string Source_Type { get; set; } = string.Empty;
        public long Source_Id { get; set; }
        public string Source_Number { get; set; } = string.Empty;
        public string Source_Status { get; set; } = string.Empty;
        public List<SourceBalanceDto> Linked_Documents { get; set; } = new();
    }
}
