using NPMotors.Services.Abstractions.ServiceInterfaces;
using System;
using System.Collections.Generic;
using System.Text;
using NPMotors.Repository.Interface;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.RequestFeatures;

namespace NPMotors.Services.Service
{
    public class CustomerService: ICustomerService
    {
        private readonly ICustomerRepository _customerRepository;

        public CustomerService(ICustomerRepository customerRepository)
        {
            _customerRepository = customerRepository;
        }

        public Task<PagedList<CustomerResponseDto>> GetAllCustomers(CustomerParameters customerParameters)
        {
            return _customerRepository.GetAllCustomers(customerParameters);
        }

        public Task<CustomerResponseDto?> GetCustomerById(long id)
        {
            return _customerRepository.GetCustomerById(id);
        }

        public Task<CustomerResponseDto> AddCustomer(CustomerCreateDto customer)
        {
            return _customerRepository.AddCustomer(customer);
        }

        public Task<CustomerResponseDto?> UpdateCustomer(long id, CustomerUpdateDto customer)
        {
            return _customerRepository.UpdateCustomer(id, customer);
        }

        public Task<bool> DeleteCustomer(long id)
        {
            return _customerRepository.DeleteCustomer(id);
        }
    }
}
