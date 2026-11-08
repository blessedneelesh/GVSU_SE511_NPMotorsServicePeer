using NPMotors.Repository.Exceptions;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.RequestFeatures;
using NPMotors.Services.Abstractions.ServiceInterfaces;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
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

        [HttpGet]
        public async Task<IActionResult> GetAllSalesInvoices([FromQuery] SalesInvoiceParameters parameters)
        {
            var invoices = await _salesInvoiceService.GetAllSalesInvoices(parameters);
            return Ok(new { items = invoices, metaData = invoices.MetaData });
        }

        [HttpGet]
        public async Task<IActionResult> GetSalesInvoiceById([FromQuery] long id)
        {
            var invoice = await _salesInvoiceService.GetSalesInvoiceById(id);
            return invoice is null ? NotFound() : Ok(invoice);
        }

        [HttpPost]
        public async Task<IActionResult> CancelSalesInvoice([FromQuery] long id, [FromBody] SalesInvoiceCancelDto reason)
        {
            try
            {
                var cancelled = await _salesInvoiceService.CancelSalesInvoice(id, reason);
                return Ok(cancelled);
            }
            catch (KeyNotFoundException)
            {
                return NotFound();
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> PostInvoice([FromQuery] long id, [FromBody] PostInvoiceRequest request)
        {
            try
            {
                var posted = await _salesInvoiceService.PostInvoice(id, request);
                return posted is null ? NotFound() : Ok(posted);
            }
            catch (ValidationException ex)
            {
                return BadRequest(new { errors = new[] { ex.Message } });
            }
            catch (ConflictException ex)
            {
                return Conflict(new { message = ex.Message });
            }
        }
    }
}