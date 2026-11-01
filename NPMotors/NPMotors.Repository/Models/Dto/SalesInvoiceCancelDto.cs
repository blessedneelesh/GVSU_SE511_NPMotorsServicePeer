using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text;

namespace NPMotors.Repository.Models.Dto
{
    public class SalesInvoiceCancelDto
    {
        [Required, StringLength(500)]
        public string Reason { get; set; } = string.Empty;
    }
}
