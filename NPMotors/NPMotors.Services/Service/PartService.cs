using NPMotors.Services.Abstractions.ServiceInterfaces;
using System;
using System.Collections.Generic;
using System.Text;
using NPMotors.Repository.Interface;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.RequestFeatures;

namespace NPMotors.Services.Service
{
    public class PartService : IPartService
    {
        private const int MaxPageSize = 50;

        private readonly IPartRepository _partRepository;

        public PartService(IPartRepository partRepository)
        {
            _partRepository = partRepository;
        }

        public async Task<PagedList<PartResponseDto>> GetAllParts(PartParameters parameters)
        {
            if (parameters.PageSize > MaxPageSize)
                parameters.PageSize = MaxPageSize;

            var parts = await _partRepository.GetAllParts(parameters);
            foreach (var part in parts)
            {
                part.Inventory_Value = CalculateInventoryValue(part);
                part.Is_Low_Stock = IsLowStock(part);
            }

            return parts;
        }

        public async Task<PartResponseDto?> GetPartById(long id)
        {
            var part = await _partRepository.GetPartById(id);
            if (part is null)
                return null;

            part.Inventory_Value = CalculateInventoryValue(part);
            part.Is_Low_Stock = IsLowStock(part);
            return part;
        }

        public async Task<PartResponseDto> AddPart(PartCreateDto part)
        {
            Validate(part.Part_Number, part.Vendor_Id, part.Cost_Price, part.Sell_Price, part.Quantity, part.Reorder_Level);

            var created = await _partRepository.AddPart(part);
            created.Inventory_Value = CalculateInventoryValue(created);
            created.Is_Low_Stock = IsLowStock(created);
            return created;
        }

        private static decimal CalculateInventoryValue(PartResponseDto part)
        {
            return part.Quantity * part.Cost_Price;
        }

        private static bool IsLowStock(PartResponseDto part)
        {
            return part.Quantity <= part.Reorder_Level;
        }

        private static void Validate(string partNumber, long vendorId, decimal? costPrice, decimal? sellPrice, int? quantity, int? reorderLevel)
        {
            if (string.IsNullOrWhiteSpace(partNumber))
                throw new ArgumentException("Part number is required.");
            if (vendorId <= 0)
                throw new ArgumentException("Vendor is required.");
            if (costPrice < 0)
                throw new ArgumentException("Cost price cannot be negative.");
            if (sellPrice < 0)
                throw new ArgumentException("Sell price cannot be negative.");
            if (quantity < 0)
                throw new ArgumentException("Quantity cannot be negative.");
            if (reorderLevel < 0)
                throw new ArgumentException("Reorder level cannot be negative.");
        }
    }
}
