namespace NPMotors.Repository.Models.Dto
{
    public class ProfitAndLossDto
    {
        public string Period_Type { get; set; } = string.Empty;
        public DateTime Start_Date { get; set; }
        public DateTime End_Date { get; set; }
        public RevenueSummaryDto Revenue { get; set; } = new();
        public CogsSummaryDto Cogs { get; set; } = new();
        public OperatingExpenseSummaryDto Operating_Expenses { get; set; } = new();
        // Calculated by ReportService.
        public decimal Total_Revenue { get; set; }
        public decimal Total_Cogs { get; set; }
        public decimal Gross_Profit { get; set; }
        public decimal Total_Operating_Expenses { get; set; }
        public decimal Net_Profit { get; set; }
        // Percent, rounded to 2 decimals; null when Total_Revenue is 0.
        public decimal? Gross_Margin { get; set; }
        public decimal? Net_Margin { get; set; }
        public DateTime Generated_Date { get; set; }
        public string Generated_By { get; set; } = string.Empty;
    }
}
