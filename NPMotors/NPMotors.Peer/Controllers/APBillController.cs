using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.RequestFeatures;
using NPMotors.Services.Abstractions.ServiceInterfaces;

namespace NPMotors.Peer.Controllers
{
    [ApiController]
    [Route("[controller]/[action]")]
    public class APBillController : ControllerBase
    {
        private readonly IAPBillService _apBillService;

        public APBillController(IAPBillService apBillService)
        {
            _apBillService = apBillService;
        }

        [HttpGet("/Ap/GetAllAp")]
        public async Task<IActionResult> GetAllAp([FromQuery] ApAgingParameters parameters)
        {
            var items = await _apBillService.GetAllAp(parameters);
            return Ok(new { items, metaData = items.MetaData });
        }

        [HttpGet("/Ap/GetApById")]
        public async Task<IActionResult> GetApById([FromQuery] long id)
        {
            var ap = await _apBillService.GetApById(id);
            return ap is null ? NotFound() : Ok(ap);
        }

        [HttpPost("{apBillId:long}")]
        public async Task<IActionResult> RecordVendorPayment(long apBillId, [FromBody] VendorPaymentCreateDto payment)
        {
            try
            {
                var bill = await _apBillService.RecordVendorPayment(apBillId, payment);
                return bill is null ? NotFound() : Ok(bill);
            }
            catch (ValidationException ex)
            {
                return BadRequest(new { errors = new[] { ex.Message } });
            }
        }

        [HttpGet("{apBillId:long}")]
        public async Task<IActionResult> GetPaymentHistory(long apBillId)
        {
            var bill = await _apBillService.GetPaymentHistory(apBillId);
            return bill is null ? NotFound() : Ok(bill);
        }
    }
}
