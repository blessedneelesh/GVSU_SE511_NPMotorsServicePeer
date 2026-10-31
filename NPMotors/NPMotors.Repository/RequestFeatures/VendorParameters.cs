using System;
using System.Collections.Generic;
using System.Text;
using NPMotors.Repository.Models.Enums;

namespace NPMotors.Repository.RequestFeatures
{
    public class VendorParameters : RequestParameters
    {
        public Status? Status { get; set; }
        public string? Type { get; set; }
        public string? SearchTerm { get; set; }
    }
}
