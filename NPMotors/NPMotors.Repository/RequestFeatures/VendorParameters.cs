using System;
using System.Collections.Generic;
using System.Text;

namespace NPMotors.Repository.RequestFeatures
{
    public class VendorParameters : RequestParameters
    {
        public string? Status { get; set; }
        public string? Type { get; set; }
        public string? SearchTerm { get; set; }
    }
}
