using System;
using System.Collections.Generic;
using System.Text;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.RequestFeatures;

namespace NPMotors.Services.Abstractions.ServiceInterfaces
{
    public interface IVehicleService
    {
        Task<VehicleResponseDto> AddVehicle(VehicleCreateDto vehicle);
        Task<PagedList<VehicleResponseDto>> GetAllVehicles(VehicleParameters parameters);
        Task<VehicleResponseDto?> GetVehicleById(long id);
    }
}
