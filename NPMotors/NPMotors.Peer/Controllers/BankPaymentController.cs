using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using NPMotors.Repository.Models.Dto;
using NPMotors.Services.Abstractions.ServiceInterfaces;

namespace NPMotors.Peer.Controllers
{
    [ApiController]
    [Route("[controller]/[action]")]
    public class BankPaymentController : ControllerBase
    {
        private readonly IBankPaymentService _bankPaymentService;

        public BankPaymentController(IBankPaymentService bankPaymentService)
        {
            _bankPaymentService = bankPaymentService;
        }

        // An invalid DTO is a 400 from [ApiController] before this runs.
        [HttpPost]
        public async Task<IActionResult> RecordMiscellaneousPayment([FromBody] MiscellaneousPaymentCreateDto payment)
        {
            try
            {
                var response = await _bankPaymentService.RecordMiscellaneousPayment(payment);
                return Ok(response);
            }
            catch (ValidationException ex)
            {
                return BadRequest(new { errors = new[] { ex.Message } });
            }
        }
    }
}
