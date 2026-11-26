namespace NPMotors.Repository.Models.Dto
{
    public class RevenueSummaryDto
    {
        public string Period_Type { get; set; } = string.Empty;
        public DateTime Start_Date { get; set; }
        public DateTime End_Date { get; set; }
        public decimal Vehicle_Revenue { get; set; }
        public decimal Service_Revenue { get; set; }
        public decimal Fee_Income { get; set; }
        public decimal Interest_Income { get; set; }
        public decimal Other_Income { get; set; }
        public decimal Discounts { get; set; }
        // Calculated by ReportService.
        public decimal Total_Revenue { get; set; }
        public int Invoice_Count { get; set; }
    }
}
