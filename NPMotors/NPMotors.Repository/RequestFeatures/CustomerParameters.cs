using System;
using System.Collections.Generic;
using System.Text;
using NPMotors.Repository.Models.Enums;

namespace NPMotors.Repository.RequestFeatures
{
    public class CustomerParameters: RequestParameters
    {
        public Status? Status { get; set; }
        public string? SearchTerm { get; set; }
    }
}
