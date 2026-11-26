using System.ComponentModel.DataAnnotations;

namespace NPMotors.Repository.Models.Dto
{
    // Story 4 vehicle fields plus the payment condition of the purchase.
    // Due_Date is used for the AP Bill when one is created.
    public class VehiclePurchaseCreateDto : VehicleCreateDto
    {
        [Required, RegularExpression("^(PaidInFull|Partial|Credit)$", ErrorMessage = "Payment condition must be PaidInFull, Partial or Credit.")]
        public string Payment_Condition { get; set; } = string.Empty;
        [Required, Range(typeof(decimal), "0", "999999999999.99", ErrorMessage = "Amount paid cannot be negative.")]
        public decimal Amount_Paid { get; set; }
        [RegularExpression("^(Cash|Check|Card|Transfer)$", ErrorMessage = "Payment method must be Cash, Check, Card or Transfer.")]
        public string? Payment_Method { get; set; }
        [StringLength(100)]
        public string? Reference { get; set; }
        public long? Bank_Account_Id { get; set; }
        public DateTime? Due_Date { get; set; }
    }
}
