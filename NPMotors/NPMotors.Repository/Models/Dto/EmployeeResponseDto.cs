using System;
using System.Collections.Generic;
using System.Text;

namespace NPMotors.Repository.Models.Dto
{
    public class EmployeeResponseDto
    {
        public long Id { get; set; }
        public string? First_Name { get; set; }
        public string? Last_Name { get; set; }
        public string? Department { get; set; }
        public string? Job { get; set; }
        public decimal? Salary { get; set; }
        public decimal? Bonus { get; set; }
        public decimal? Commission_Rate { get; set; }
        public string? Status { get; set; }
        public bool Is_Salesperson { get; set; }
    }
}
