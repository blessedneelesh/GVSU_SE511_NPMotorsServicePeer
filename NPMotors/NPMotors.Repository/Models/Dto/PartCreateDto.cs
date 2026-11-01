using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text;

namespace NPMotors.Repository.Models.Dto
{
    public class PartCreateDto
    {
        [Required, StringLength(50)]
        public string Part_Number { get; set; }
        [StringLength(100)]
        public string? Name { get; set; }
        [StringLength(50)]
        public string? Category { get; set; }
        [Required]
        public long Vendor_Id { get; set; }
        [Range(typeof(decimal), "0", "999999999999.99", ErrorMessage = "Cost price cannot be negative.")]
        public decimal? Cost_Price { get; set; }
        [Range(typeof(decimal), "0", "999999999999.99", ErrorMessage = "Sell price cannot be negative.")]
        public decimal? Sell_Price { get; set; }
        [Range(0, int.MaxValue, ErrorMessage = "Quantity cannot be negative.")]
        public int? Quantity { get; set; }
        [Range(0, int.MaxValue, ErrorMessage = "Reorder level cannot be negative.")]
        public int? Reorder_Level { get; set; }
        [StringLength(100)]
        public string? Warehouse { get; set; }
    }
}
