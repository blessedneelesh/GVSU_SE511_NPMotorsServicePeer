using NPMotors.Services.Abstractions.ServiceInterfaces;
using System;
using System.Collections.Generic;
using System.Text;
using NPMotors.Repository.Interface;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.Models.Enums;
using NPMotors.Repository.RequestFeatures;

namespace NPMotors.Services.Service
{
    public class VehicleService : IVehicleService
    {
        private const int MaxPageSize = 50;

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

        public async Task<PagedList<VehicleResponseDto>> GetAllVehicles(VehicleParameters parameters)
        {
            var status = parameters.Status?.Trim();
            if (!string.IsNullOrEmpty(status)
                && !status.Equals("available", StringComparison.OrdinalIgnoreCase)
                && !status.Equals("reserved", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Status must be 'available' or 'reserved'.");

            if (parameters.PageSize > MaxPageSize)
                parameters.PageSize = MaxPageSize;

            var vehicles = await _vehicleRepository.GetAllVehicles(parameters);
            for (var i = 0; i < vehicles.Count; i++)
                vehicles[i] = CalculateProfit(vehicles[i]);

            return vehicles;
        }

        public async Task<VehicleResponseDto?> GetVehicleById(long id)
        {
            var vehicle = await _vehicleRepository.GetVehicleById(id);
            return vehicle is null ? null : CalculateProfit(vehicle);
        }

        private static VehicleResponseDto CalculateProfit(VehicleResponseDto vehicle)
        {
            vehicle.Total_Cost = vehicle.Purchase_Price + vehicle.Additional_Costs;
            vehicle.Gross_Profit = vehicle.Selling_Price - vehicle.Total_Cost;
            vehicle.Gross_Margin = vehicle.Selling_Price == 0
                ? null
                : vehicle.Gross_Profit / vehicle.Selling_Price * 100;
            vehicle.Is_Available = vehicle.Status == VehicleStatus.Available;
            return vehicle;
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
