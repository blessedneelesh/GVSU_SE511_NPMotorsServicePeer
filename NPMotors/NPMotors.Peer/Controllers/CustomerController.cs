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
    public class CustomerController : ControllerBase
    {
        private readonly ICustomerService _customerService;
        public CustomerController(ICustomerService customerService)
        {
            _customerService = customerService;
        }
        [HttpGet]
        public async Task<IActionResult> GetAllCustomers([FromQuery] CustomerParameters customerParameters)
        {
            var customers = await _customerService.GetAllCustomers(customerParameters);
            return Ok(new { items = customers, metaData = customers.MetaData });
        }

        [HttpGet]
        public async Task<IActionResult> GetCustomerById([FromQuery] long id)
        {
            var customer = await _customerService.GetCustomerById(id);
            return customer is null ? NotFound() : Ok(customer);
        }

        [HttpPost]
        public async Task<IActionResult> AddCustomer([FromBody] CustomerCreateDto customer)
        {
            var created = await _customerService.AddCustomer(customer);
            return StatusCode(201, created);
        }

        [HttpPut]
        public async Task<IActionResult> UpdateCustomer([FromQuery] long id, [FromBody] CustomerUpdateDto customer)
        {
            var updated = await _customerService.UpdateCustomer(id, customer);
            return updated is null ? NotFound() : Ok(updated);
        }

        [HttpDelete]
        public async Task<IActionResult> DeleteCustomer([FromQuery] long id)
        {
            var deleted = await _customerService.DeleteCustomer(id);
            return deleted ? NoContent() : NotFound();
        }
    }
}
