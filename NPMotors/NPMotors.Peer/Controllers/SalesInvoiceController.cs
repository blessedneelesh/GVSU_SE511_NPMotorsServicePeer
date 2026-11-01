using NPMotors.Repository.Models.Dto;
using NPMotors.Services.Abstractions.ServiceInterfaces;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.Text;
namespace NPMotors.Peer.Controllers
{
    [ApiController]
    [Route("[controller]/[action]")]
    public class SalesInvoiceController : ControllerBase
    {
        private readonly ISalesInvoiceService _salesInvoiceService;
        public SalesInvoiceController(ISalesInvoiceService salesInvoiceService)
        {
            _salesInvoiceService = salesInvoiceService;
        }

        [HttpPost]
        public async Task<IActionResult> AddSalesInvoice([FromBody] SalesInvoiceCreateDto invoice)
        {
            try
            {
                var created = await _salesInvoiceService.AddSalesInvoice(invoice);
                return StatusCode(201, created);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> PostSalesInvoice([FromBody] SalesInvoiceCreateDto invoice)
        {
            try
            {
                var posted = await _salesInvoiceService.PostSalesInvoice(invoice);
                return StatusCode(201, posted);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }
    }
}
