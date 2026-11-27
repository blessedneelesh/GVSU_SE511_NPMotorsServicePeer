using Microsoft.AspNetCore.Mvc;
using NPMotors.Services.Abstractions.ServiceInterfaces;

namespace NPMotors.Peer.Controllers
{
    [ApiController]
    [Route("[controller]/[action]")]
    public class DashboardController : ControllerBase
    {
        private readonly IDashboardService _dashboardService;

        public DashboardController(IDashboardService dashboardService)
        {
            _dashboardService = dashboardService;
        }

        // An unparseable As_Of_Date is rejected by model binding (400 ProblemDetails).
        [HttpGet]
        public async Task<IActionResult> GetSummary([FromQuery(Name = "As_Of_Date")] DateTime? asOfDate)
        {
            var summary = await _dashboardService.GetSummary(asOfDate);
            return Ok(summary);
        }
    }
}
