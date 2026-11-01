using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text;

namespace NPMotors.Repository.Models.Dto
{
    public class VehicleCreateDto
    {
        [Required]
        public long Vendor_Id { get; set; }
        [Required, StringLength(17)]
        public string Vin { get; set; }
        [StringLength(50)]
        public string? Make { get; set; }
        [StringLength(50)]
        public string? Model { get; set; }
        public int? Year { get; set; }
        [StringLength(30)]
        public string? Color { get; set; }
        public DateTime? Purchase_Date { get; set; }
        [StringLength(100)]
        public string? Warehouse { get; set; }
        [Required, Range(typeof(decimal), "0.01", "999999999999.99", ErrorMessage = "Purchase price must be greater than zero.")]
        public decimal Purchase_Price { get; set; }
        [Range(typeof(decimal), "0", "999999999999.99", ErrorMessage = "Additional costs cannot be negative.")]
        public decimal? Additional_Costs { get; set; }
        [Range(typeof(decimal), "0", "999999999999.99", ErrorMessage = "Selling price cannot be negative.")]
        public decimal? Selling_Price { get; set; }
    }
}
