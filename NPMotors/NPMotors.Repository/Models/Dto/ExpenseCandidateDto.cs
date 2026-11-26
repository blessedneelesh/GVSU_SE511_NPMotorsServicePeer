namespace NPMotors.Repository.Models.Dto
{
    public class ExpenseCandidateDto
    {
        // "Payment" | "Adjustment"
        public string Source_Type { get; set; } = string.Empty;
        public long Source_Id { get; set; }
        public string Source_Number { get; set; } = string.Empty;
        public DateTime Date { get; set; }
        public decimal Amount { get; set; }
        public string? Payment_Category { get; set; }
        public string? Adjustment_Type { get; set; }
        public long? Ap_Bill_Id { get; set; }
        public long? Vehicle_Id { get; set; }
        public bool Is_Reversed { get; set; }
    }
}
