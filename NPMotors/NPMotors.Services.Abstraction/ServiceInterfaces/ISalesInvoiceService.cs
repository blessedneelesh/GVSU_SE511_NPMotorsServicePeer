using System;
using System.Collections.Generic;
using System.Text;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.RequestFeatures;

namespace NPMotors.Services.Abstractions.ServiceInterfaces
{
    public interface ISalesInvoiceService
    {
        Task<SalesInvoiceResponseDto> AddSalesInvoice(SalesInvoiceCreateDto invoice);
        Task<SalesInvoiceResponseDto> PostSalesInvoice(SalesInvoiceCreateDto invoice);
        Task<PagedList<SalesInvoiceResponseDto>> GetAllSalesInvoices(SalesInvoiceParameters parameters);
        Task<SalesInvoiceResponseDto?> GetSalesInvoiceById(long id);
        Task<SalesInvoiceResponseDto> CancelSalesInvoice(long id, SalesInvoiceCancelDto reason);
        Task<SalesInvoiceResponseDto?> PostInvoice(long id, PostInvoiceRequest request);
    }
}
