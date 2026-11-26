using NPMotors.Services.Abstractions.ServiceInterfaces;
using System;
using System.Collections.Generic;
using System.Text;
using NPMotors.Repository.Interface;
using NPMotors.Repository.Models;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.Models.Enums;
using NPMotors.Repository.RequestFeatures;

namespace NPMotors.Services.Service
{
    public class VehicleService : IVehicleService
    {
        private const int MaxPageSize = 50;
        private const string PaidInFull = "PaidInFull";
        private const string Partial = "Partial";
        private const string Credit = "Credit";

        private readonly IVehicleRepository _vehicleRepository;

        public VehicleService(IVehicleRepository vehicleRepository)
        {
            _vehicleRepository = vehicleRepository;
        }

        public async Task<VehicleResponseDto> AddVehicle(VehicleCreateDto vehicle)
        {
            ValidateAmounts(vehicle);
            var vin = vehicle.Vin?.Trim();
            if (string.IsNullOrWhiteSpace(vin))
                throw new ArgumentException("VIN is required.");
            if (vin.Length != 17)
                throw new ArgumentException("VIN must be exactly 17 characters.");

            vehicle.Vin = vin;

            if (await _vehicleRepository.VinExists(vin))
                throw new ArgumentException("A vehicle with this VIN already exists.");

            await EnsureVendorIsActive(vehicle.Vendor_Id);

            return await _vehicleRepository.AddVehicle(vehicle);
        }

        // Story 4 rules still apply (active vendor, unique VIN, price > 0, costs not negative), then the
        // payment condition is validated, the AP Bill is prepared, and the repository saves everything together.
        public async Task<VehiclePurchaseResponseDto> PurchaseVehicle(VehiclePurchaseCreateDto purchase)
        {
            ValidateAmounts(purchase);
            var vin = purchase.Vin?.Trim();
            if (string.IsNullOrWhiteSpace(vin))
                throw new ArgumentException("VIN is required.");
            if (vin.Length != 17)
                throw new ArgumentException("VIN must be exactly 17 characters.");

            purchase.Vin = vin;

            if (await _vehicleRepository.VinExists(vin))
                throw new ArgumentException("A vehicle with this VIN already exists.");

            await EnsureVendorIsActive(purchase.Vendor_Id);

            var totalCost = purchase.Purchase_Price + (purchase.Additional_Costs ?? 0m);

            ValidateAmountPaid(purchase, totalCost);
            ApplyPaymentCondition(purchase, totalCost);
            var apBill = CreateApBillIfBalance(purchase, totalCost);

            var result = await _vehicleRepository.PurchaseVehicle(purchase, apBill);

            result.Balance_Due = result.Total_Cost - result.Amount_Paid;
            result.Payment_Status = DeterminePaymentStatus(result.Amount_Paid, result.Total_Cost);
            return result;
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

        // 0 <= Amount_Paid <= totalCost; when Amount_Paid > 0 a payment method and bank account are required.
        private static void ValidateAmountPaid(VehiclePurchaseCreateDto purchase, decimal totalCost)
        {
            if (purchase.Amount_Paid < 0)
                throw new ArgumentException("Amount paid cannot be negative.");
            if (purchase.Amount_Paid > totalCost)
                throw new ArgumentException("Amount paid cannot exceed the vehicle's total cost.");

            if (purchase.Amount_Paid > 0)
            {
                if (string.IsNullOrWhiteSpace(purchase.Payment_Method))
                    throw new ArgumentException("Payment method is required when an amount is paid.");
                if (!Enum.TryParse<PaymentMethod>(purchase.Payment_Method.Trim(), true, out var method)
                    || !Enum.IsDefined(method))
                    throw new ArgumentException("Payment method must be Cash, Check, Card or Transfer.");
                if (purchase.Bank_Account_Id is null)
                    throw new ArgumentException("A bank account is required when an amount is paid.");
            }
        }

        // The condition must match the amount: PaidInFull -> Amount_Paid = totalCost; Partial -> 0 < Amount_Paid < totalCost;
        // Credit -> Amount_Paid = 0.
        private static void ApplyPaymentCondition(VehiclePurchaseCreateDto purchase, decimal totalCost)
        {
            var condition = purchase.Payment_Condition?.Trim();
            if (string.Equals(condition, PaidInFull, StringComparison.OrdinalIgnoreCase))
            {
                if (purchase.Amount_Paid != totalCost)
                    throw new ArgumentException("A vehicle paid in full must have an amount paid equal to its total cost.");
                purchase.Payment_Condition = PaidInFull;
            }
            else if (string.Equals(condition, Partial, StringComparison.OrdinalIgnoreCase))
            {
                if (purchase.Amount_Paid <= 0 || purchase.Amount_Paid >= totalCost)
                    throw new ArgumentException("A partial payment must be greater than zero and less than the total cost.");
                purchase.Payment_Condition = Partial;
            }
            else if (string.Equals(condition, Credit, StringComparison.OrdinalIgnoreCase))
            {
                if (purchase.Amount_Paid != 0)
                    throw new ArgumentException("A vehicle bought on credit must have an amount paid of zero.");
                purchase.Payment_Condition = Credit;
            }
            else
            {
                throw new ArgumentException("Payment condition must be PaidInFull, Partial or Credit.");
            }
        }

        // "paid" when paid equals total, "partial" when 0 < paid < total, "unpaid" when paid is 0.
        private static string DeterminePaymentStatus(decimal paid, decimal total)
        {
            if (paid == total)
                return "paid";
            return paid > 0 ? "partial" : "unpaid";
        }

        // null when nothing is owed (PaidInFull); otherwise the AP Bill for the full vehicle cost.
        private static APBill? CreateApBillIfBalance(VehiclePurchaseCreateDto purchase, decimal totalCost)
        {
            if (totalCost - purchase.Amount_Paid == 0)
                return null;

            return new APBill
            {
                VendorId = purchase.Vendor_Id,
                Amount = totalCost,
                Paid = purchase.Amount_Paid,
                Balance = totalCost - purchase.Amount_Paid,
                Status = DeterminePaymentStatus(purchase.Amount_Paid, totalCost),
                Date = (purchase.Purchase_Date ?? DateTime.Today).Date,
                DueDate = purchase.Due_Date
            };
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
