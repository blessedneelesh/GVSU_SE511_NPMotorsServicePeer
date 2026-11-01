using NPMotors.Services.Abstractions.ServiceInterfaces;
using System;
using System.Collections.Generic;
using System.Text;
using NPMotors.Repository.Interface;
using NPMotors.Repository.Models.Dto;

namespace NPMotors.Services.Service
{
    public class VehicleService : IVehicleService
    {
        private readonly IVehicleRepository _vehicleRepository;

        public VehicleService(IVehicleRepository vehicleRepository)
        {
            _vehicleRepository = vehicleRepository;
        }

        public async Task<VehicleResponseDto> AddVehicle(VehicleCreateDto vehicle)
        {
            ValidateAmounts(vehicle);
            if (string.IsNullOrWhiteSpace(vehicle.Vin))
                throw new ArgumentException("VIN is required.");

            await EnsureVendorIsActive(vehicle.Vendor_Id);

            return await _vehicleRepository.AddVehicle(vehicle);
        }

        private async Task EnsureVendorIsActive(long vendorId)
        {
            var status = await _vehicleRepository.GetVendorStatus(vendorId);
            if (status is null)
                throw new ArgumentException("Vendor does not exist.");
            if (status != "active")
                throw new ArgumentException("Inactive vendors cannot be selected.");
        }

        private static void ValidateAmounts(VehicleCreateDto vehicle)
        {
            if (vehicle.Purchase_Price <= 0)
                throw new ArgumentException("Purchase price must be greater than zero.");
            if (vehicle.Additional_Costs < 0)
                throw new ArgumentException("Additional costs cannot be negative.");
            if (vehicle.Selling_Price < 0)
                throw new ArgumentException("Selling price cannot be negative.");
        }
    }
}
