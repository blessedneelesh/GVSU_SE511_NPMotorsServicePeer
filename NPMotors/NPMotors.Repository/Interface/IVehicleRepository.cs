using System;
using System.Collections.Generic;
using System.Text;
using NPMotors.Repository.Models.Dto;

namespace NPMotors.Repository.Interface
{
    public interface IVehicleRepository
    {
        Task<VehicleResponseDto> AddVehicle(VehicleCreateDto vehicle);
        Task<string?> GetVendorStatus(long vendorId);
        Task<bool> DeleteVehicle(long id);
    }
}
