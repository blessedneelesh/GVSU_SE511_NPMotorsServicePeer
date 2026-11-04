using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text;

namespace NPMotors.Repository.Models.Dto
{
    // Receiving stock: the amount is added to the part's current quantity.
    public class PartUpdateDto
    {
        [Required]
        [Range(1, int.MaxValue, ErrorMessage = "Quantity received must be greater than zero.")]
        public int? Quantity_Received { get; set; }
    }
}
