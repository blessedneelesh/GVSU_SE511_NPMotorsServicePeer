using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.RequestFeatures;
using NPMotors.Services.Abstractions.ServiceInterfaces;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.Text;
namespace NPMotors.Peer.Controllers
{
    [ApiController]
    [Route("[controller]/[action]")]
    public class PartController : ControllerBase
    {
        private readonly IPartService _partService;
        public PartController(IPartService partService)
        {
            _partService = partService;
        }

        [HttpGet]
        public async Task<IActionResult> GetAllParts([FromQuery] PartParameters parameters)
        {
            var parts = await _partService.GetAllParts(parameters);
            return Ok(new { items = parts, metaData = parts.MetaData });
        }

        [HttpGet]
        public async Task<IActionResult> GetPartById([FromQuery] long id)
        {
            var part = await _partService.GetPartById(id);
            return part is null ? NotFound() : Ok(part);
        }

        [HttpPost]
        public async Task<IActionResult> AddPart([FromBody] PartCreateDto part)
        {
            try
            {
                var created = await _partService.AddPart(part);
                return StatusCode(201, created);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPut]
        public async Task<IActionResult> UpdatePart([FromQuery] long id, [FromBody] PartUpdateDto part)
        {
            try
            {
                var updated = await _partService.UpdatePart(id, part);
                return updated is null ? NotFound() : Ok(updated);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }
    }
}
