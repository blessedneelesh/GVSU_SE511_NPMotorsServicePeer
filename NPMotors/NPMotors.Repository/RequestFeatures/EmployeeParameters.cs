using System;
using System.Collections.Generic;
using System.Text;

namespace NPMotors.Repository.RequestFeatures
{
    public class EmployeeParameters : RequestParameters
    {
        public string? Status { get; set; }
        public string? Department { get; set; }
        public string? SearchTerm { get; set; }
        public bool? IsSalesperson { get; set; }
    }
}
