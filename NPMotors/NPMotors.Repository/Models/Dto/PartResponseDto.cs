using System;
using System.Collections.Generic;
using System.Text;

namespace NPMotors.Repository.Models.Dto
{
    public class PartResponseDto
    {
        public long Id { get; set; }
        public string Part_Number { get; set; }
        public string? Name { get; set; }
        public string? Category { get; set; }
        public long Vendor_Id { get; set; }
        public decimal Cost_Price { get; set; }
        public decimal Sell_Price { get; set; }
        public int Quantity { get; set; }
        public int Reorder_Level { get; set; }
        public string? Warehouse { get; set; }
        // Calculated (Quantity x Cost_Price), read-only.
        public decimal Inventory_Value { get; set; }
        // Calculated (Quantity <= Reorder_Level), read-only.
        public bool Is_Low_Stock { get; set; }
    }
}
