using System.ComponentModel.DataAnnotations;

namespace NPMotors.Repository.Models.Dto
{
    public class VendorPaymentCreateDto
    {
        [Required]
        public DateTime Payment_Date { get; set; }

        [Required]
        [Range(typeof(decimal), "0.01", "999999999999.99", ErrorMessage = "Amount must be greater than zero.")]
        public decimal Amount { get; set; }

        // Cash | Check | Card | Transfer
        [Required]
        public string Payment_Method { get; set; } = string.Empty;

        [MaxLength(100)]
        public string? Reference { get; set; }

        [Required]
        public long Bank_Account_Id { get; set; }
    }
}
