using System.ComponentModel.DataAnnotations;

namespace NPMotors.Repository.Models.Dto
{
    public class ReverseTransactionDto
    {
        [Required]
        [MaxLength(500)]
        public string Reason { get; set; } = string.Empty;
    }
}
