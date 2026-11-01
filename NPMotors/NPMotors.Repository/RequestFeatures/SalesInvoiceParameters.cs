using System;
using System.Collections.Generic;
using System.Text;

namespace NPMotors.Repository.RequestFeatures
{
    public class SalesInvoiceParameters : RequestParameters
    {
        // Matches the invoice number, the customer name or the vehicle VIN.
        public string? SearchTerm { get; set; }
        // "draft" | "posted" | "cancelled" (empty = all, cancelled invoices included).
        public string? Lifecycle { get; set; }
        // Payment state: "unpaid" | "partial" | "paid" | "overdue".
        public string? Status { get; set; }
        public DateTime? FromDate { get; set; }
        public DateTime? ToDate { get; set; }
    }
}
