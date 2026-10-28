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
    public class VendorController : ControllerBase
    {
        private readonly IVendorService _vendorService;
        public VendorController(IVendorService vendorService)
        {
            _vendorService = vendorService;
        }
        [HttpGet]
        public async Task<IActionResult> GetAllVendors([FromQuery] VendorParameters vendorParameters)
        {
            var vendors = await _vendorService.GetAllVendors(vendorParameters);
            return Ok(new { items = vendors, metaData = vendors.MetaData });
        }

        [HttpGet]
        public async Task<IActionResult> GetVendorById([FromQuery] long id)
        {
            var vendor = await _vendorService.GetVendorById(id);
            return vendor is null ? NotFound() : Ok(vendor);
        }

        [HttpGet]
        public async Task<IActionResult> GetActiveVendors([FromQuery] string? type)
        {
            var vendors = await _vendorService.GetActiveVendors(type);
            return Ok(vendors);
        }

        [HttpPost]
        public async Task<IActionResult> AddVendor([FromBody] VendorCreateDto vendor)
        {
            var created = await _vendorService.AddVendor(vendor);
            return StatusCode(201, created);
        }

        [HttpPut]
        public async Task<IActionResult> UpdateVendor([FromQuery] long id, [FromBody] VendorUpdateDto vendor)
        {
            var updated = await _vendorService.UpdateVendor(id, vendor);
            return updated is null ? NotFound() : Ok(updated);
        }

        [HttpDelete]
        public async Task<IActionResult> DeleteVendor([FromQuery] long id)
        {
            var result = await _vendorService.DeleteVendor(id);
            return result switch
            {
                DeleteVendorResult.Deleted => NoContent(),
                DeleteVendorResult.Referenced => Conflict(new { message = "Vendor is referenced by parts, vehicles, bills or payments and cannot be deleted. Set the status to inactive instead." }),
                _ => NotFound()
            };
        }
    }
}
