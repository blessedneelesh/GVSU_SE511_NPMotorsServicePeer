using NPMotors.Services.Abstractions.ServiceInterfaces;
using System;
using System.Collections.Generic;
using System.Text;
using NPMotors.Repository.Interface;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.RequestFeatures;

namespace NPMotors.Services.Service
{
    public class VendorService : IVendorService
    {
        private readonly IVendorRepository _vendorRepository;

        private static readonly string[] AllowedTypes = { "Vehicle", "Parts", "Service" };

        public VendorService(IVendorRepository vendorRepository)
        {
            _vendorRepository = vendorRepository;
        }

        public Task<PagedList<VendorResponseDto>> GetAllVendors(VendorParameters vendorParameters)
        {
            return _vendorRepository.GetAllVendors(vendorParameters);
        }

        public Task<VendorResponseDto?> GetVendorById(long id)
        {
            return _vendorRepository.GetVendorById(id);
        }

        public Task<IEnumerable<VendorResponseDto>> GetActiveVendors(string? type = null)
        {
            return _vendorRepository.GetActiveVendors(type);
        }

        public Task<VendorResponseDto> AddVendor(VendorCreateDto vendor)
        {
            vendor.Type = NormalizeType(vendor.Type);
            vendor.Status = NormalizeStatus(vendor.Status);
            Validate(vendor.Name);
            return _vendorRepository.AddVendor(vendor);
        }

        public Task<VendorResponseDto?> UpdateVendor(long id, VendorUpdateDto vendor)
        {
            vendor.Type = NormalizeType(vendor.Type);
            vendor.Status = NormalizeStatus(vendor.Status);
            Validate(vendor.Name);
            return _vendorRepository.UpdateVendor(id, vendor);
        }

        public async Task<DeleteVendorResult> DeleteVendor(long id)
        {
            if (await _vendorRepository.IsVendorReferenced(id))
            {
                return DeleteVendorResult.Referenced;
            }

            return await _vendorRepository.DeleteVendor(id)
                ? DeleteVendorResult.Deleted
                : DeleteVendorResult.NotFound;
        }

        private static void Validate(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Vendor name is required.");
        }

        private static string NormalizeType(string? type)
        {
            var match = AllowedTypes.FirstOrDefault(t => t.Equals(type?.Trim(), StringComparison.OrdinalIgnoreCase));
            return match ?? throw new ArgumentException("Type must be 'Vehicle', 'Parts' or 'Service'.");
        }

        private static string NormalizeStatus(string? status)
        {
            var value = (status ?? "active").Trim().ToLowerInvariant();
            if (value != "active" && value != "inactive")
                throw new ArgumentException("Status must be 'active' or 'inactive'.");
            return value;
        }
    }
}
