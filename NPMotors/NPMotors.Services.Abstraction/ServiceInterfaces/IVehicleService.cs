using System;
using System.Collections.Generic;
using System.Text;
using NPMotors.Repository.Models.Dto;

namespace NPMotors.Services.Abstractions.ServiceInterfaces
{
    public interface IVehicleService
    {
        Task<VehicleResponseDto> AddVehicle(VehicleCreateDto vehicle);
    }
}
