using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text;
using NPMotors.Repository.Models.Enums;

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
        [EnumDataType(typeof(VendorType), ErrorMessage = "Type must be 'Vehicle', 'Parts' or 'Service'.")]
        public VendorType Type { get; set; }
        public Status Status { get; set; } = Status.Active;
    }
}
