using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using NPMotors.Repository.Models.Dto;
using NPMotors.Services.Abstractions.ServiceInterfaces;

namespace NPMotors.Peer.Controllers
{
    [ApiController]
    [Route("[controller]/[action]")]
    public class ReportController : ControllerBase
    {
        private readonly IReportService _reportService;

        public ReportController(IReportService reportService)
        {
            _reportService = reportService;
        }

        [HttpGet]
        public async Task<IActionResult> GetRevenue([FromQuery] ReportPeriodDto period)
        {
            try
            {
                var summary = await _reportService.GetRevenue(period);
                return Ok(summary);
            }
            catch (ValidationException ex)
            {
                return BadRequest(new { errors = new[] { ex.Message } });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetCostOfGoodsSold([FromQuery] ReportPeriodDto period)
        {
            try
            {
                var summary = await _reportService.GetCostOfGoodsSold(period);
                return Ok(summary);
            }
            catch (ValidationException ex)
            {
                return BadRequest(new { errors = new[] { ex.Message } });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetOperatingExpenses([FromQuery] ReportPeriodDto period)
        {
            try
            {
                var summary = await _reportService.GetOperatingExpenses(period);
                return Ok(summary);
            }
            catch (ValidationException ex)
            {
                return BadRequest(new { errors = new[] { ex.Message } });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetProfitAndLoss([FromQuery] ReportPeriodDto period)
        {
            try
            {
                var report = await _reportService.GetProfitAndLoss(period, User.Identity?.Name ?? string.Empty);
                return Ok(report);
            }
            catch (ValidationException ex)
            {
                return BadRequest(new { errors = new[] { ex.Message } });
            }
        }

        [HttpGet]
        public async Task<IActionResult> ComparePeriods([FromQuery] ComparePeriodsDto comparison)
        {
            try
            {
                var result = await _reportService.ComparePeriods(comparison, User.Identity?.Name ?? string.Empty);
                return Ok(result);
            }
            catch (ValidationException ex)
            {
                return BadRequest(new { errors = new[] { ex.Message } });
            }
        }
    }
}
