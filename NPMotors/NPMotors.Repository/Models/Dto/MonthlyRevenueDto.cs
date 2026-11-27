namespace NPMotors.Repository.Models.Dto
{
    public class MonthlyRevenueDto
    {
        // "yyyy-MM".
        public string Month { get; set; } = string.Empty;
        public decimal Revenue { get; set; }
    }
}
