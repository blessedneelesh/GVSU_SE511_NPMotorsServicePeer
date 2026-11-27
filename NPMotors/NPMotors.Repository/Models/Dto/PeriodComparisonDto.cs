namespace NPMotors.Repository.Models.Dto
{
    public class PeriodComparisonDto
    {
        public ProfitAndLossDto Current { get; set; } = new();
        public ProfitAndLossDto Previous { get; set; } = new();
        // Calculated by ReportService. Percent, rounded to 2 decimals; null ("unavailable") when the previous amount is 0.
        public decimal? Revenue_Change_Pct { get; set; }
        public decimal? Cogs_Change_Pct { get; set; }
        public decimal? Gross_Profit_Change_Pct { get; set; }
        public decimal? Operating_Expense_Change_Pct { get; set; }
        public decimal? Net_Profit_Change_Pct { get; set; }
        // Percentage points (current margin - previous margin); null when either margin is null.
        public decimal? Gross_Margin_Change { get; set; }
        public decimal? Net_Margin_Change { get; set; }
    }
}
