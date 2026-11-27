namespace NPMotors.Repository.Models.Dto
{
    public class DashboardSummaryDto
    {
        public DateOnly As_Of_Date { get; set; }
        public DateOnly Period_Start { get; set; }
        public DateOnly Period_End { get; set; }

        public int Total_Inventory { get; set; }
        public int Available_Vehicles { get; set; }
        public List<MakeCountDto> Available_By_Make { get; set; } = new();

        public int Vehicles_Sold { get; set; }
        public int Vehicles_Sold_Previous { get; set; }
        public decimal Revenue { get; set; }
        public decimal Revenue_Previous { get; set; }
        // Null when Revenue_Previous is 0 (as ComparePeriods).
        public decimal? Revenue_Change_Pct { get; set; }

        public decimal Receivable_Balance { get; set; }
        public int Open_Receivables { get; set; }
        public int Active_Customers { get; set; }

        public List<MonthlyRevenueDto> Monthly_Revenue { get; set; } = new();
        public List<SalesInvoiceResponseDto> Recent_Invoices { get; set; } = new();
    }
}
