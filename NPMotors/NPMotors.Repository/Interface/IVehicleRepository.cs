using System;
using System.Collections.Generic;
using System.Text;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.RequestFeatures;

namespace NPMotors.Repository.Interface
{
    public interface IVehicleRepository
    {
        Task<VehicleResponseDto> AddVehicle(VehicleCreateDto vehicle);
        Task<bool> VinExists(string vin);
        Task<string?> GetVendorStatus(long vendorId);
        Task<bool> DeleteVehicle(long id);
        Task<PagedList<VehicleResponseDto>> GetAllVehicles(VehicleParameters parameters);
        Task<VehicleResponseDto?> GetVehicleById(long id);
    }
}
