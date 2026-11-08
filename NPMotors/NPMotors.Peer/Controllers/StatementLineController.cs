using Microsoft.AspNetCore.Mvc;
using NPMotors.Repository.RequestFeatures;
using NPMotors.Services.Abstractions.ServiceInterfaces;

namespace NPMotors.Peer.Controllers
{
    [ApiController]
    [Route("[controller]/[action]")]
    public class StatementLineController : ControllerBase
    {
        private readonly IStatementLineService _statementLineService;

        public StatementLineController(IStatementLineService statementLineService)
        {
            _statementLineService = statementLineService;
        }

        [HttpGet]
        public async Task<IActionResult> GetStatementLines([FromQuery] StatementLineParameters parameters)
        {
            var lines = await _statementLineService.GetStatementLines(parameters);
            return Ok(new { items = lines, metaData = lines.MetaData });
        }
    }
}
