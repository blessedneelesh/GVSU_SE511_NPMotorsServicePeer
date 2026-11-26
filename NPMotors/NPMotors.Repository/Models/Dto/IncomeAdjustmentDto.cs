namespace NPMotors.Repository.Models.Dto
{
    public class IncomeAdjustmentDto
    {
        public long Transaction_Id { get; set; }
        public string Number { get; set; } = string.Empty;
        public DateTime Date { get; set; }
        public decimal Amount { get; set; }
        public string Adjustment_Type { get; set; } = string.Empty;
    }
}
