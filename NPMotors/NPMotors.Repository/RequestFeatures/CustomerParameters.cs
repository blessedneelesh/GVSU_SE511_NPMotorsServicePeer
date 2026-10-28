using System;
using System.Collections.Generic;
using System.Text;

namespace NPMotors.Repository.RequestFeatures
{
    public class CustomerParameters: RequestParameters
    {
        public string? Status { get; set; }
        public string? SearchTerm { get; set; }
    }
}
