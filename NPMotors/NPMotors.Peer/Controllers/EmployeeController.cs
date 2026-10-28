using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.RequestFeatures;
using NPMotors.Services.Abstractions.ServiceInterfaces;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.Text;
namespace NPMotors.Peer.Controllers
{
    [ApiController]
    [Route("[controller]/[action]")]
    public class EmployeeController : ControllerBase
    {
        private readonly IEmployeeService _employeeService;
        public EmployeeController(IEmployeeService employeeService)
        {
            _employeeService = employeeService;
        }
        [HttpGet]
        public async Task<IActionResult> GetAllEmployees([FromQuery] EmployeeParameters employeeParameters)
        {
            var employees = await _employeeService.GetAllEmployees(employeeParameters);
            return Ok(new { items = employees, metaData = employees.MetaData });
        }

        [HttpGet]
        public async Task<IActionResult> GetEmployeeById([FromQuery] long id)
        {
            var employee = await _employeeService.GetEmployeeById(id);
            return employee is null ? NotFound() : Ok(employee);
        }

        [HttpGet]
        public async Task<IActionResult> GetActiveSalespeople()
        {
            var salespeople = await _employeeService.GetActiveSalespeople();
            return Ok(salespeople);
        }

        [HttpPost]
        public async Task<IActionResult> AddEmployee([FromBody] EmployeeCreateDto employee)
        {
            var created = await _employeeService.AddEmployee(employee);
            return StatusCode(201, created);
        }

        [HttpPut]
        public async Task<IActionResult> UpdateEmployee([FromQuery] long id, [FromBody] EmployeeUpdateDto employee)
        {
            var updated = await _employeeService.UpdateEmployee(id, employee);
            return updated is null ? NotFound() : Ok(updated);
        }

        [HttpDelete]
        public async Task<IActionResult> DeleteEmployee([FromQuery] long id)
        {
            var result = await _employeeService.DeleteEmployee(id);
            return result switch
            {
                DeleteEmployeeResult.Deleted => NoContent(),
                DeleteEmployeeResult.Referenced => Conflict(new { message = "Employee is referenced by sales invoices and cannot be deleted. Set the status to inactive instead." }),
                _ => NotFound()
            };
        }
    }
}
