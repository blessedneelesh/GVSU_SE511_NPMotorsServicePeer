using NPMotors.Services.Abstractions.ServiceInterfaces;
using System;
using System.Collections.Generic;
using System.Text;
using NPMotors.Repository.Interface;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.RequestFeatures;

namespace NPMotors.Services.Service
{
    public class EmployeeService : IEmployeeService
    {
        private readonly IEmployeeRepository _employeeRepository;

        public EmployeeService(IEmployeeRepository employeeRepository)
        {
            _employeeRepository = employeeRepository;
        }

        public Task<PagedList<EmployeeResponseDto>> GetAllEmployees(EmployeeParameters employeeParameters)
        {
            return _employeeRepository.GetAllEmployees(employeeParameters);
        }

        public Task<EmployeeResponseDto?> GetEmployeeById(long id)
        {
            return _employeeRepository.GetEmployeeById(id);
        }

        public Task<IEnumerable<EmployeeResponseDto>> GetActiveSalespeople()
        {
            return _employeeRepository.GetActiveSalespeople();
        }

        public Task<EmployeeResponseDto> AddEmployee(EmployeeCreateDto employee)
        {
            Validate(employee.First_Name, employee.Last_Name, employee.Salary, employee.Bonus, employee.Commission_Rate);
            return _employeeRepository.AddEmployee(employee);
        }

        public Task<EmployeeResponseDto?> UpdateEmployee(long id, EmployeeUpdateDto employee)
        {
            Validate(employee.First_Name, employee.Last_Name, employee.Salary, employee.Bonus, employee.Commission_Rate);
            return _employeeRepository.UpdateEmployee(id, employee);
        }

        public async Task<DeleteEmployeeResult> DeleteEmployee(long id)
        {
            if (await _employeeRepository.IsEmployeeReferenced(id))
            {
                return DeleteEmployeeResult.Referenced;
            }

            return await _employeeRepository.DeleteEmployee(id)
                ? DeleteEmployeeResult.Deleted
                : DeleteEmployeeResult.NotFound;
        }

        private static void Validate(string firstName, string lastName, decimal? salary, decimal? bonus, decimal? commissionRate)
        {
            if (string.IsNullOrWhiteSpace(firstName))
                throw new ArgumentException("First name is required.");
            if (string.IsNullOrWhiteSpace(lastName))
                throw new ArgumentException("Last name is required.");
            if (salary < 0)
                throw new ArgumentException("Salary cannot be negative.");
            if (bonus < 0)
                throw new ArgumentException("Bonus cannot be negative.");
            if (commissionRate < 0 || commissionRate > 100)
                throw new ArgumentException("Commission rate must be between 0 and 100.");
        }
    }
}
