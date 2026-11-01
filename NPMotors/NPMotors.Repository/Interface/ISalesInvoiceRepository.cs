using System;
using System.Collections.Generic;
using System.Text;
using NPMotors.Repository.Models.Dto;

namespace NPMotors.Repository.Interface
{
    public interface ISalesInvoiceRepository
    {
        Task<SalesInvoiceResponseDto> AddSalesInvoice(SalesInvoiceCreateDto invoice);
        Task<SalesInvoiceResponseDto> PostSalesInvoice(SalesInvoiceCreateDto invoice);
    }
}
