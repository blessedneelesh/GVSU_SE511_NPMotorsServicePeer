using System;
using System.Collections.Generic;
using System.Text;

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
        public string? Type { get; set; }
        public decimal Balance { get; set; }
        public decimal TotalPurchases { get; set; }
        public string? Status { get; set; }
    }
}
