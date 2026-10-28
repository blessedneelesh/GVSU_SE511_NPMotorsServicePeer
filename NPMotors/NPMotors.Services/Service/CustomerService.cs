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
    }
}
