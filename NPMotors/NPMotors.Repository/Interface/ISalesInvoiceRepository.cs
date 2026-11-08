using System;
using System.Collections.Generic;
using System.Data;
using System.Text;
using NPMotors.Repository.Models;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.RequestFeatures;

namespace NPMotors.Repository.Interface
{
    public interface ISalesInvoiceRepository
    {
        Task<SalesInvoiceResponseDto> AddSalesInvoice(SalesInvoiceCreateDto invoice);
        Task<SalesInvoiceResponseDto> PostSalesInvoice(SalesInvoiceCreateDto invoice);
        Task<PagedList<SalesInvoiceResponseDto>> GetAllSalesInvoices(SalesInvoiceParameters parameters);
        Task<SalesInvoiceResponseDto?> GetSalesInvoiceById(long id);
        Task<SalesInvoiceResponseDto> CancelSalesInvoice(long id, SalesInvoiceCancelDto reason);
        Task<SalesInvoice?> GetInvoiceById(long id);
        Task<int> UpdatePostedInvoice(SalesInvoice invoice, IDbTransaction tx);
    }
}
