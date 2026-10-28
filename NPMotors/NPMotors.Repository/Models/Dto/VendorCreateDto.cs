using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text;

namespace NPMotors.Repository.Models.Dto
{
    public class VendorCreateDto
    {
        [Required, StringLength(150)]
        public string Name { get; set; }
        [StringLength(150)]
        public string? Contact { get; set; }
        [StringLength(30)]
        public string? Phone { get; set; }
        [EmailAddress, StringLength(255)]
        public string? Email { get; set; }
        [StringLength(255)]
        public string? Address { get; set; }
        [Required, RegularExpression("(?i)^(vehicle|parts|service)$", ErrorMessage = "Type must be 'Vehicle', 'Parts' or 'Service'.")]
        public string Type { get; set; }
        [RegularExpression("(?i)^(active|inactive)$", ErrorMessage = "Status must be 'active' or 'inactive'.")]
        public string Status { get; set; } = "active";
    }
}
