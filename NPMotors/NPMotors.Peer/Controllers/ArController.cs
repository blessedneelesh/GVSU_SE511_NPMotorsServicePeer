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
        public async Task<IActionResult> GetAllAr([FromQuery] ArAgingParameters parameters)
        {
            var items = await _arService.GetAllAr(parameters);
            return Ok(new { items, metaData = items.MetaData });
        }

        [HttpGet]
        public async Task<IActionResult> GetArById([FromQuery] long id)
        {
            var ar = await _arService.GetArById(id);
            return ar is null ? NotFound() : Ok(ar);
        }
    }
}
