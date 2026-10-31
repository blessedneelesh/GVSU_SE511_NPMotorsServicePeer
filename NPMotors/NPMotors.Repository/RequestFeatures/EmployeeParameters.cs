using System;
using System.Collections.Generic;
using System.Text;
using NPMotors.Repository.Models.Enums;

namespace NPMotors.Repository.RequestFeatures
{
    public class EmployeeParameters : RequestParameters
    {
        public Status? Status { get; set; }
        public string? Department { get; set; }
        public string? SearchTerm { get; set; }
        public bool? IsSalesperson { get; set; }
    }
}
