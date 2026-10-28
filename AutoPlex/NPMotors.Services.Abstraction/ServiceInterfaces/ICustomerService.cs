using System;
using System.Collections.Generic;
using System.Text;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.RequestFeatures;

namespace NPMotors.Services.Abstractions.ServiceInterfaces
{
    public interface ICustomerService
    {
        Task<PagedList<CustomerResponseDto>> GetAllCustomers(CustomerParameters customerParameters);
    }
}
