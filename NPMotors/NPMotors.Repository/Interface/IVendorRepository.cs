using System;
using System.Collections.Generic;
using System.Text;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.RequestFeatures;

namespace NPMotors.Repository.Interface
{
    public interface IVendorRepository
    {
        Task<PagedList<VendorResponseDto>> GetAllVendors(VendorParameters vendorParameters);
        Task<VendorResponseDto?> GetVendorById(long id);
        Task<IEnumerable<VendorResponseDto>> GetActiveVendors(string? type = null);
        Task<VendorResponseDto> AddVendor(VendorCreateDto vendor);
        Task<VendorResponseDto?> UpdateVendor(long id, VendorUpdateDto vendor);
        Task<bool> DeleteVendor(long id);
        Task<bool> IsVendorReferenced(long id);
    }
}
