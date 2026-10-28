using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text;

namespace NPMotors.Repository.Models.Dto
{
    public class EmployeeUpdateDto
    {
        [Required, StringLength(100)]
        public string First_Name { get; set; }
        [Required, StringLength(100)]
        public string Last_Name { get; set; }
        [StringLength(100)]
        public string? Department { get; set; }
        [StringLength(100)]
        public string? Job { get; set; }
        [Range(typeof(decimal), "0", "999999999999.99", ErrorMessage = "Salary cannot be negative.")]
        public decimal? Salary { get; set; }
        [Range(typeof(decimal), "0", "999999999999.99", ErrorMessage = "Bonus cannot be negative.")]
        public decimal? Bonus { get; set; }
        [Range(typeof(decimal), "0", "100", ErrorMessage = "Commission rate must be between 0 and 100.")]
        public decimal? Commission_Rate { get; set; }
        [RegularExpression("(?i)^(active|inactive)$", ErrorMessage = "Status must be 'active' or 'inactive'.")]
        public string Status { get; set; } = "active";
    }
}
