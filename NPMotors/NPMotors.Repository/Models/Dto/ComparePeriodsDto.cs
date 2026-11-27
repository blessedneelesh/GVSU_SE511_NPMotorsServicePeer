using System.ComponentModel.DataAnnotations;

namespace NPMotors.Repository.Models.Dto
{
    public class ComparePeriodsDto
    {
        [Required, RegularExpression("^(Month|Quarter|YearToDate)$", ErrorMessage = "Period_Type must be Month, Quarter or YearToDate.")]
        public string Period_Type { get; set; } = string.Empty;

        [Required]
        public DateTime? Current_Anchor_Date { get; set; }

        // When empty, the previous anchor is one month (Month), three months (Quarter) or one year (YearToDate) before the current one.
        public DateTime? Previous_Anchor_Date { get; set; }
    }
}
