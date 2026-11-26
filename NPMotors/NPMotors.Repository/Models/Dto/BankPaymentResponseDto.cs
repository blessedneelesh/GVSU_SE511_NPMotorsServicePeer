namespace NPMotors.Repository.Models.Dto
{
    public class BankPaymentResponseDto
    {
        public long Id { get; set; }
        public string Payment_Number { get; set; } = string.Empty;
        public DateTime Payment_Date { get; set; }
        public string Payee { get; set; } = string.Empty;
        public string Payment_Category { get; set; } = string.Empty;
        public long? Vendor_Id { get; set; }
        public string Payment_Method { get; set; } = string.Empty;
        public string? Reference { get; set; }
        public string? Description { get; set; }
        public decimal Amount { get; set; }
        public long Bank_Account_Id { get; set; }
        public long Transaction_Id { get; set; }
        // {readOnly}: BankAccount.BookBalance after the payment (balance before - Amount); not stored on bank_payment.
        public decimal Book_Balance { get; set; }
        public string Status { get; set; } = string.Empty;
    }
}
