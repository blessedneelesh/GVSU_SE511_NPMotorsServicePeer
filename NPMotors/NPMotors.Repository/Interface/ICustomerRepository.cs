using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.RequestFeatures;
using System;
using System.Collections.Generic;
using System.Data;
using System.Text;

namespace NPMotors.Repository.Interface
{
    public interface ICustomerRepository
    {
        Task<PagedList<CustomerResponseDto>> GetAllCustomers(CustomerParameters customerParameters);
        Task<CustomerResponseDto?> GetCustomerById(long id);
        Task<CustomerResponseDto> AddCustomer(CustomerCreateDto customer);
        Task<CustomerResponseDto?> UpdateCustomer(long id, CustomerUpdateDto customer);
        Task<bool> DeleteCustomer(long id);
        Task<IEnumerable<decimal>> GetOpenArBalances(long customerId, IDbTransaction tx);
        Task<IEnumerable<decimal>> GetPostedInvoiceTotals(long customerId, IDbTransaction tx);
        Task<int> UpdateCustomerTotals(long customerId, decimal balance, decimal totalPurchases, IDbTransaction tx);
    }
}
