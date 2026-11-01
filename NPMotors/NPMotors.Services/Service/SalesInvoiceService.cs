using NPMotors.Services.Abstractions.ServiceInterfaces;
using System;
using System.Collections.Generic;
using System.Text;
using NPMotors.Repository.Interface;
using NPMotors.Repository.Models.Dto;

namespace NPMotors.Services.Service
{
    public class SalesInvoiceService : ISalesInvoiceService
    {
        private readonly ISalesInvoiceRepository _salesInvoiceRepository;

        public SalesInvoiceService(ISalesInvoiceRepository salesInvoiceRepository)
        {
            _salesInvoiceRepository = salesInvoiceRepository;
        }

        public async Task<SalesInvoiceResponseDto> AddSalesInvoice(SalesInvoiceCreateDto invoice)
        {
            ValidateInvoice(invoice);

            var created = await _salesInvoiceRepository.AddSalesInvoice(invoice);
            created.Total = CalculateTotal(created.Sale_Price, created.Tax, created.Fees, created.Discount);
            created.Balance = CalculateBalance(created.Total, created.Paid);
            return created;
        }

        public async Task<SalesInvoiceResponseDto> PostSalesInvoice(SalesInvoiceCreateDto invoice)
        {
            ValidateInvoice(invoice);

            var posted = await _salesInvoiceRepository.PostSalesInvoice(invoice);
            posted.Total = CalculateTotal(posted.Sale_Price, posted.Tax, posted.Fees, posted.Discount);
            posted.Balance = CalculateBalance(posted.Total, posted.Paid);
            return posted;
        }

        private static decimal CalculateTotal(decimal salePrice, decimal tax, decimal fees, decimal discount)
        {
            return salePrice + tax + fees - discount;
        }

        private static decimal CalculateBalance(decimal total, decimal paid)
        {
            return total - paid;
        }

        private static void ValidateInvoice(SalesInvoiceCreateDto invoice)
        {
            if (invoice.Customer_Id <= 0)
                throw new ArgumentException("Customer is required.");
            if (invoice.Salesperson_Id <= 0)
                throw new ArgumentException("Salesperson is required.");
            if (invoice.Vehicle_Id <= 0)
                throw new ArgumentException("Vehicle is required.");
            if (invoice.Invoice_Date == default)
                throw new ArgumentException("Invoice date is required.");
            if (invoice.Tax < 0)
                throw new ArgumentException("Tax cannot be negative.");
            if (invoice.Fees < 0)
                throw new ArgumentException("Fees cannot be negative.");
            if (invoice.Discount < 0)
                throw new ArgumentException("Discount cannot be negative.");
        }
    }
}
