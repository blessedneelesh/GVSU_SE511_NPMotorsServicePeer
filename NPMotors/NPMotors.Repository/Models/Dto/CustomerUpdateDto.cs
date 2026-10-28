using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text;

namespace NPMotors.Repository.Models.Dto
{
    public class CustomerUpdateDto
    {
        [Required, StringLength(100)]
        public string First_Name { get; set; }
        [Required, StringLength(100)]
        public string Last_Name { get; set; }
        [StringLength(30)]
        public string? Phone { get; set; }
        [Required, EmailAddress, StringLength(255)]
        public string Email { get; set; }
        [StringLength(255)]
        public string? Address { get; set; }
        [StringLength(100)]
        public string? City { get; set; }
        [StringLength(50)]
        public string? State { get; set; }
        [StringLength(20)]
        public string? Zip { get; set; }
        [RegularExpression("(?i)^(active|inactive)$", ErrorMessage = "Status must be 'active' or 'inactive'.")]
        public string Status { get; set; } = "active";
    }
}
