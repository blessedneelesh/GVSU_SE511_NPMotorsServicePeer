using System;
using System.Collections.Generic;
using System.Text;

namespace NPMotors.Repository.RequestFeatures
{
    public class VehicleParameters : RequestParameters
    {
        // "available" | "reserved" (empty = all). "sold" is added in Story 8.
        public string? Status { get; set; }
        public string? SearchTerm { get; set; }
    }
}
