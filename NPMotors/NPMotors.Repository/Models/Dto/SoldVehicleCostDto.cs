namespace NPMotors.Repository.Models.Dto
{
    public class SoldVehicleCostDto
    {
        public long Vehicle_Id { get; set; }
        public string Vin { get; set; } = string.Empty;
        public long Invoice_Id { get; set; }
        public string Invoice_Number { get; set; } = string.Empty;
        public DateTime Invoice_Date { get; set; }
        public decimal Total_Cost { get; set; }
    }
}
