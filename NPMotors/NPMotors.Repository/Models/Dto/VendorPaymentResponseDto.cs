namespace NPMotors.Repository.Models.Dto
{
    public class VendorPaymentResponseDto
    {
        public long Id { get; set; }
        public string Payment_Number { get; set; } = string.Empty;
        public DateTime Payment_Date { get; set; }
        public string Payment_Method { get; set; } = string.Empty;
        public string? Reference { get; set; }
        public decimal Amount { get; set; }
        public long Bank_Account_Id { get; set; }
        public long Transaction_Id { get; set; }
        public string Status { get; set; } = string.Empty;
    }
}
