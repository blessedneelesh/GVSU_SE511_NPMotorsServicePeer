using System;
using System.Collections.Generic;
using System.Text;
using NPMotors.Repository.Models.Enums;

namespace NPMotors.Repository.Models.Dto
{
    public class VehicleResponseDto
    {
        public long Id { get; set; }
        public long Vendor_Id { get; set; }
        public string Vendor_Name { get; set; } = string.Empty;
        public string Vin { get; set; }
        public string? Make { get; set; }
        public string? Model { get; set; }
        public int? Year { get; set; }
        public string? Color { get; set; }
        public DateTime? Purchase_Date { get; set; }
        public string? Warehouse { get; set; }
        public decimal Purchase_Price { get; set; }
        public decimal Additional_Costs { get; set; }
        // Calculated (purchase_price + additional_costs), read-only.
        public decimal Total_Cost { get; set; }
        public decimal Selling_Price { get; set; }
        // Calculated (selling_price - total_cost), read-only.
        public decimal Gross_Profit { get; set; }
        // Calculated (gross_profit / selling_price * 100), null when selling_price is 0, read-only.
        public decimal? Gross_Margin { get; set; }
        // Calculated (status == available), read-only.
        public bool Is_Available { get; set; }
        public VehicleStatus Status { get; set; }
    }
}
