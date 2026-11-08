using Microsoft.AspNetCore.Mvc;
using NPMotors.Repository.RequestFeatures;
using NPMotors.Services.Abstractions.ServiceInterfaces;

namespace NPMotors.Peer.Controllers
{
    [ApiController]
    [Route("[controller]/[action]")]
    public class ArController : ControllerBase
    {
        private readonly IArService _arService;

        public ArController(IArService arService)
        {
            _arService = arService;
        }

        [HttpGet]
        public async Task<IActionResult> GetArAging([FromQuery] ArAgingParameters parameters)
        {
            var items = await _arService.GetArAging(parameters);
            return Ok(new { items, metaData = items.MetaData });
        }
    }
}
