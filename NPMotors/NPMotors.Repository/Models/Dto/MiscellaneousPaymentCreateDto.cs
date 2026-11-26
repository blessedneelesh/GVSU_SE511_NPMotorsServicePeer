using System.ComponentModel.DataAnnotations;

namespace NPMotors.Repository.Models.Dto
{
    // A payment not linked to AP. Payee may be a vendor, an employee or another party; Vendor_Id is optional
    // (when given it must exist) and never changes Vendor.Balance, which only AP bills do.
    public class MiscellaneousPaymentCreateDto
    {
        [Required]
        [MaxLength(100)]
        public string Payee { get; set; } = string.Empty;

        // Operating-expense category (e.g. Rent, Utilities, Supplies, Payroll).
        [Required]
        [MaxLength(50)]
        public string Payment_Category { get; set; } = string.Empty;

        public long? Vendor_Id { get; set; }

        [Required]
        public DateTime Payment_Date { get; set; }

        [Required]
        [Range(typeof(decimal), "0.01", "999999999999.99", ErrorMessage = "Amount must be greater than zero.")]
        public decimal Amount { get; set; }

        // Cash | Check | Card | Transfer
        [Required]
        [RegularExpression("(?i)^(Cash|Check|Card|Transfer)$", ErrorMessage = "Payment method must be Cash, Check, Card or Transfer.")]
        public string Payment_Method { get; set; } = string.Empty;

        [Required]
        public long Bank_Account_Id { get; set; }

        [MaxLength(100)]
        public string? Reference { get; set; }

        [MaxLength(200)]
        public string? Description { get; set; }
    }
}
