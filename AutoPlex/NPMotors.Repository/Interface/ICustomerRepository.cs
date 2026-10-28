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
    }
}
