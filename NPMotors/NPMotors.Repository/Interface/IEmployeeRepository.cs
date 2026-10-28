using System;
using System.Collections.Generic;
using System.Text;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.RequestFeatures;

namespace NPMotors.Repository.Interface
{
    public interface IEmployeeRepository
    {
        Task<PagedList<EmployeeResponseDto>> GetAllEmployees(EmployeeParameters employeeParameters);
        Task<EmployeeResponseDto?> GetEmployeeById(long id);
        Task<IEnumerable<EmployeeResponseDto>> GetActiveSalespeople();
        Task<EmployeeResponseDto> AddEmployee(EmployeeCreateDto employee);
        Task<EmployeeResponseDto?> UpdateEmployee(long id, EmployeeUpdateDto employee);
        Task<bool> DeleteEmployee(long id);
        Task<bool> IsEmployeeReferenced(long id);
    }
}
