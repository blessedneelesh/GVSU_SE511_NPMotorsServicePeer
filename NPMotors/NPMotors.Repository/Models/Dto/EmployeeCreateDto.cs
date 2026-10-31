using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text;
using NPMotors.Repository.Models.Enums;

namespace NPMotors.Repository.Models.Dto
{
    public class EmployeeCreateDto
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
        public Status Status { get; set; } = Status.Active;
    }
}
