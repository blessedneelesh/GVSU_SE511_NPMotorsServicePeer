namespace NPMotors.Repository.Models.Dto
{
    public class SalesRevenueTotalsDto
    {
        public decimal Vehicle_Revenue { get; set; }
        public decimal Service_Revenue { get; set; }
        public decimal Fee_Income { get; set; }
        public decimal Discounts { get; set; }
        public int Invoice_Count { get; set; }
    }
}
