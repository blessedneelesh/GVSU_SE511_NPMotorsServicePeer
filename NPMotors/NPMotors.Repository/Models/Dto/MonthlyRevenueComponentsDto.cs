namespace NPMotors.Repository.Models.Dto
{
    // The stored revenue parts of one calendar month; DashboardService combines them like ReportService.
    public class MonthlyRevenueComponentsDto
    {
        // First day of the month.
        public DateTime Month { get; set; }
        public decimal Vehicle_Revenue { get; set; }
        public decimal Fee_Income { get; set; }
        public decimal Discounts { get; set; }
        public decimal Interest_Income { get; set; }
        public decimal Other_Income { get; set; }
    }
}
