using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text;
using NPMotors.Repository.Models.Enums;

namespace NPMotors.Repository.Models.Dto
{
    public class BankAccountUpdateDto
    {
        [Range(1, long.MaxValue, ErrorMessage = "A bank account must belong to a bank.")]
        public long BankId { get; set; }
        [Required, StringLength(150)]
        public string Name { get; set; }
        [StringLength(50)]
        public string? AccountNumber { get; set; }
        [StringLength(20)]
        public string? Routing { get; set; }
        [Required, RegularExpression("(?i)^(checking|savings|other)$", ErrorMessage = "Type must be 'Checking', 'Savings' or 'Other'.")]
        public string Type { get; set; }
        public Status Status { get; set; } = Status.Active;
    }
}
