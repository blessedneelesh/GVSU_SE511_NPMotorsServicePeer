using NPMotors.Repository.Models.Dto;
using NPMotors.Services.Abstractions.ServiceInterfaces;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.Text;
namespace NPMotors.Peer.Controllers
{
    [ApiController]
    [Route("[controller]/[action]")]
    public class VehicleController : ControllerBase
    {
        private readonly IVehicleService _vehicleService;
        public VehicleController(IVehicleService vehicleService)
        {
            _vehicleService = vehicleService;
        }

        [HttpPost]
        public async Task<IActionResult> AddVehicle([FromBody] VehicleCreateDto vehicle)
        {
            try
            {
                var created = await _vehicleService.AddVehicle(vehicle);
                return StatusCode(201, created);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }
    }
}
