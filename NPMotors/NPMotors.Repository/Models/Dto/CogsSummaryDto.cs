namespace NPMotors.Repository.Models.Dto
{
    public class CogsSummaryDto
    {
        public string Period_Type { get; set; } = string.Empty;
        public DateTime Start_Date { get; set; }
        public DateTime End_Date { get; set; }
        public decimal Vehicle_Cogs { get; set; }
        public decimal Parts_Cogs { get; set; }
        // Calculated by ReportService.
        public decimal Total_Cogs { get; set; }
        public int Vehicles_Sold_Count { get; set; }
    }
}
