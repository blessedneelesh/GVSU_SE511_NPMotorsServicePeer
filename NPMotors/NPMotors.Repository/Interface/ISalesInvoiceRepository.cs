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
        Task<SalesInvoiceResponseDto> AddSalesInvoice(SalesInvoiceCreateDto invoice, IDbTransaction tx);
        Task<SalesInvoiceResponseDto?> UpdateDraftInvoice(long id, SalesInvoiceCreateDto invoice);
        Task<SalesInvoiceResponseDto> PostSalesInvoice(SalesInvoiceCreateDto invoice);
        Task<PagedList<SalesInvoiceResponseDto>> GetAllSalesInvoices(SalesInvoiceParameters parameters);
        Task<SalesInvoiceResponseDto?> GetSalesInvoiceById(long id);
        Task<List<SalesInvoiceByCustomerDto>> GetSalesInvoiceByCustomer(long customerId);
        Task<SalesInvoiceResponseDto> CancelSalesInvoice(long id, SalesInvoiceCancelDto reason, IDbTransaction tx);
        Task<SalesInvoice?> GetInvoiceById(long id);
        Task<SalesInvoice?> GetInvoiceById(long id, IDbTransaction tx);
        Task<int> UpdatePostedInvoice(SalesInvoice invoice, IDbTransaction tx);
        Task<int> UpdateInvoicePayment(SalesInvoice invoice, IDbTransaction tx);
    }
}
