using System;
using System.Collections.Generic;
using System.Text;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.RequestFeatures;

namespace NPMotors.Services.Abstractions.ServiceInterfaces
{
    public enum DeleteVendorResult
    {
        Deleted,
        NotFound,
        Referenced
    }

    public interface IVendorService
    {
        Task<PagedList<VendorResponseDto>> GetAllVendors(VendorParameters vendorParameters);
        Task<VendorResponseDto?> GetVendorById(long id);
        Task<IEnumerable<VendorResponseDto>> GetActiveVendors(string? type = null);
        Task<VendorResponseDto> AddVendor(VendorCreateDto vendor);
        Task<VendorResponseDto?> UpdateVendor(long id, VendorUpdateDto vendor);
        Task<DeleteVendorResult> DeleteVendor(long id);
    }
}
