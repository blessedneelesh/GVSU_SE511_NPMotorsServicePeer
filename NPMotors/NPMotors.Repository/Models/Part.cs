using System;
using System.Collections.Generic;
using System.Text;

namespace NPMotors.Repository.Models
{
    public class Part
    {
        public long Id { get; set; }
        public string PartNumber { get; set; }
        public string? Name { get; set; }
        public string? Category { get; set; }
        public long VendorId { get; set; }
        public decimal CostPrice { get; set; }
        public decimal SellPrice { get; set; }
        public int Quantity { get; set; }
        public int ReorderLevel { get; set; }
        public string? Warehouse { get; set; }
    }
}
