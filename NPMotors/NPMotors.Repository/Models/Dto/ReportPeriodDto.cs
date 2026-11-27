using System.ComponentModel.DataAnnotations;

namespace NPMotors.Repository.Models.Dto
{
    public class ReportPeriodDto
    {
        [Required, RegularExpression("^(Month|Quarter|YearToDate)$", ErrorMessage = "Period_Type must be Month, Quarter or YearToDate.")]
        public string Period_Type { get; set; } = string.Empty;

        [Required]
        public DateTime? Anchor_Date { get; set; }
    }
}
