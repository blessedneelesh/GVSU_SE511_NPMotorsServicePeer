namespace NPMotors.Repository.Models.Dto
{
    public class VehiclePurchaseResponseDto : VehicleResponseDto
    {
        public string Payment_Condition { get; set; } = string.Empty;
        public decimal Amount_Paid { get; set; }
        // Calculated (Total_Cost - Amount_Paid), read-only.
        public decimal Balance_Due { get; set; }
        // Calculated by DeterminePaymentStatus ("paid" / "partial" / "unpaid"), read-only.
        public string Payment_Status { get; set; } = string.Empty;
        public long? Ap_Bill_Id { get; set; }
        public string? Ap_Bill_Number { get; set; }
        public long? Bank_Payment_Id { get; set; }
        public string? Payment_Number { get; set; }
        public long? Transaction_Id { get; set; }
        public decimal? Book_Balance { get; set; }
    }
}
