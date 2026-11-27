namespace NPMotors.Repository.Models.Dto
{
    public class OperatingExpenseSummaryDto
    {
        public string Period_Type { get; set; } = string.Empty;
        public DateTime Start_Date { get; set; }
        public DateTime End_Date { get; set; }
        public decimal Salary_Expense { get; set; }
        public decimal Bonus_Expense { get; set; }
        public decimal Commission_Expense { get; set; }
        public decimal Categorized_Payments { get; set; }
        public decimal Bank_Charges { get; set; }
        // Calculated by ReportService.
        public decimal Total_Operating_Expenses { get; set; }
    }
}
