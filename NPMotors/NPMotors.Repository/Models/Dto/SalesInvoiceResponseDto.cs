using System;
using System.Collections.Generic;
using System.Text;
using NPMotors.Repository.Models.Enums;

namespace NPMotors.Repository.Models.Dto
{
    public class SalesInvoiceResponseDto
    {
        public long Id { get; set; }
        public string Number { get; set; } = string.Empty;
        public DateTime Invoice_Date { get; set; }
        public DateTime? Due_Date { get; set; }
        public long Customer_Id { get; set; }
        public string Customer_Name { get; set; } = string.Empty;
        public long Salesperson_Id { get; set; }
        public long Vehicle_Id { get; set; }
        public string Vin { get; set; } = string.Empty;
        public string? Make { get; set; }
        public string? Model { get; set; }
        public string Vehicle_Status { get; set; } = string.Empty;
        public decimal Selling_Price { get; set; }
        // Inventory cost: vehicle.total_cost (Story 4).
        public decimal Vehicle_Total_Cost { get; set; }
        public decimal Sale_Price { get; set; }
        public decimal Tax { get; set; }
        public decimal Fees { get; set; }
        public decimal Discount { get; set; }
        // Calculated (sale_price + tax + fees - discount), read-only.
        public decimal Total { get; set; }
        public decimal Paid { get; set; }
        // Calculated (total - paid), read-only.
        public decimal Balance { get; set; }
        public InvoiceLifecycle Lifecycle { get; set; }
        // Null until the invoice is Posted (Paid / Partial / Unpaid / Overdue).
        public PaymentStatus? Payment_Status { get; set; }
        // Set when the invoice is Cancelled (Story 9).
        public string? Cancel_Reason { get; set; }
        // Set by Story 14 when posting leaves a balance / money is received.
        public long? Ar_Invoice_Id { get; set; }
        public long? Bank_Receipt_Id { get; set; }
    }
}
