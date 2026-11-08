using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using NPMotors.Repository.Exceptions;
using NPMotors.Repository.Models.Dto;
using NPMotors.Services.Abstractions.ServiceInterfaces;

namespace NPMotors.Peer.Controllers
{
    [ApiController]
    [Route("[controller]/[action]")]
    public class ReceiptController : ControllerBase
    {
        private readonly IReceiptService _receiptService;

        public ReceiptController(IReceiptService receiptService)
        {
            _receiptService = receiptService;
        }

        [HttpPost("{arInvoiceId:long}")]
        public async Task<IActionResult> RecordArPayment(long arInvoiceId, [FromBody] RecordArPaymentRequest request)
        {
            try
            {
                var ar = await _receiptService.RecordArPayment(arInvoiceId, request);
                return ar is null ? NotFound() : Ok(ar);
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

        [HttpGet("{arInvoiceId:long}")]
        public async Task<IActionResult> GetPaymentHistory(long arInvoiceId)
        {
            var ar = await _receiptService.GetPaymentHistory(arInvoiceId);
            return ar is null ? NotFound() : Ok(ar);
        }
    }
}
