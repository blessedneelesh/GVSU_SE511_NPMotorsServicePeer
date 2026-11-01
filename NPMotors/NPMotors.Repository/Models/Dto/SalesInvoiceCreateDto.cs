using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text;

namespace NPMotors.Repository.Models.Dto
{
    public class SalesInvoiceCreateDto
    {
        [Required]
        public long Customer_Id { get; set; }
        [Required]
        public long Salesperson_Id { get; set; }
        [Required]
        public long Vehicle_Id { get; set; }
        [Required]
        public DateTime Invoice_Date { get; set; }
        public DateTime? Due_Date { get; set; }
        [Required, Range(typeof(decimal), "0", "999999999999.99", ErrorMessage = "Sale price cannot be negative.")]
        public decimal Sale_Price { get; set; }
        [Range(typeof(decimal), "0", "999999999999.99", ErrorMessage = "Tax cannot be negative.")]
        public decimal Tax { get; set; }
        [Range(typeof(decimal), "0", "999999999999.99", ErrorMessage = "Fees cannot be negative.")]
        public decimal Fees { get; set; }
        [Range(typeof(decimal), "0", "999999999999.99", ErrorMessage = "Discount cannot be negative.")]
        public decimal Discount { get; set; }
    }
}
