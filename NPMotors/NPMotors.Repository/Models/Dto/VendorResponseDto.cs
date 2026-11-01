using System;
using System.Collections.Generic;
using System.Text;
using NPMotors.Repository.Models.Enums;

namespace NPMotors.Repository.Models.Dto
{
    public class VendorResponseDto
    {
        public long Id { get; set; }
        public string Name { get; set; }
        public string? Contact { get; set; }
        public string? Phone { get; set; }
        public string? Email { get; set; }
        public string? Address { get; set; }
        public VendorType? Type { get; set; }
        public decimal Balance { get; set; }
        public decimal TotalPurchases { get; set; }
        public Status? Status { get; set; }
    }
}
