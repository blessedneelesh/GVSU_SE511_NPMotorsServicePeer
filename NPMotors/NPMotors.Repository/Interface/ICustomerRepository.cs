using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.RequestFeatures;
using System;
using System.Collections.Generic;
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
    }
}
