using NPMotors.Repository.Interface;
using System;
using System.Collections.Generic;
using System.Data;
using System.Text;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.RequestFeatures;

namespace NPMotors.Repository.Repositories
{
    public class PartRepository : IPartRepository
    {
        private readonly IRepositoryContext _repositoryContext;

        public PartRepository(IRepositoryContext repositoryContext)
        {
            _repositoryContext = repositoryContext;
        }

        // Stored columns only; Inventory_Value and Is_Low_Stock are filled by PartService.
        private const string PartColumns =
            "id, part_number, name, category, vendor_id, COALESCE(cost_price, 0) AS Cost_Price, " +
            "COALESCE(sell_price, 0) AS Sell_Price, quantity, COALESCE(reorder_level, 0) AS Reorder_Level, warehouse";

        private const string ActiveVendorExists =
            "EXISTS (SELECT 1 FROM vendor WHERE id = @VendorId AND LOWER(status) = 'active')";

        public async Task<PagedList<PartResponseDto>> GetAllParts(PartParameters parameters)
        {
            using IDbConnection connection = _repositoryContext.GetDatabaseConnection();

            var whereClauses = new List<string>();
            var queryParams = new DynamicParameters();

            var searchTerm = parameters.SearchTerm?.Trim();
            if (!string.IsNullOrWhiteSpace(searchTerm))
            {
                whereClauses.Add("(LOWER(part_number) LIKE @SearchTerm OR LOWER(name) LIKE @SearchTerm)");
                queryParams.Add("SearchTerm", $"%{searchTerm.ToLowerInvariant()}%");
            }

            var category = parameters.Category?.Trim();
            if (!string.IsNullOrWhiteSpace(category))
            {
                whereClauses.Add("category = @Category");
                queryParams.Add("Category", category);
            }

            if (parameters.LowStockOnly == true)
                whereClauses.Add("quantity <= COALESCE(reorder_level, 0)");

            var whereSql = whereClauses.Count > 0
                ? " WHERE " + string.Join(" AND ", whereClauses)
                : string.Empty;

            var count = await connection.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM part{whereSql}", queryParams);

            queryParams.Add("Offset", (parameters.PageNumber - 1) * parameters.PageSize);
            queryParams.Add("Limit", parameters.PageSize);

            var parts = (await connection.QueryAsync<PartResponseDto>(
                $"SELECT {PartColumns} FROM part{whereSql} ORDER BY part_number, id OFFSET @Offset LIMIT @Limit",
                queryParams)).ToList();

            return new PagedList<PartResponseDto>(parts, count, parameters.PageNumber, parameters.PageSize);
        }

        public async Task<PartResponseDto?> GetPartById(long id)
        {
            using IDbConnection connection = _repositoryContext.GetDatabaseConnection();

            return await connection.QuerySingleOrDefaultAsync<PartResponseDto>(
                $"SELECT {PartColumns} FROM part WHERE id = @Id",
                new { Id = id });
        }

        public async Task<PartResponseDto> AddPart(PartCreateDto part)
        {
            using IDbConnection connection = _repositoryContext.GetDatabaseConnection();

            var queryParams = new DynamicParameters();
            queryParams.Add("PartNumber", part.Part_Number.Trim());
            queryParams.Add("Name", part.Name?.Trim());
            queryParams.Add("Category", part.Category?.Trim());
            queryParams.Add("VendorId", part.Vendor_Id);
            queryParams.Add("CostPrice", part.Cost_Price ?? 0m);
            queryParams.Add("SellPrice", part.Sell_Price ?? 0m);
            queryParams.Add("Quantity", part.Quantity ?? 0);
            queryParams.Add("ReorderLevel", part.Reorder_Level ?? 0);
            queryParams.Add("Warehouse", part.Warehouse?.Trim());

            // Inserts only when the vendor exists and is active.
            var created = await connection.QuerySingleOrDefaultAsync<PartResponseDto>(
                $@"INSERT INTO part (part_number, name, category, vendor_id, cost_price, sell_price, quantity, reorder_level, warehouse)
                   SELECT CAST(@PartNumber AS VARCHAR), CAST(@Name AS VARCHAR), CAST(@Category AS VARCHAR), @VendorId,
                          @CostPrice, @SellPrice, @Quantity, @ReorderLevel, CAST(@Warehouse AS VARCHAR)
                   WHERE {ActiveVendorExists}
                   RETURNING {PartColumns}",
                queryParams);

            if (created is null)
                throw new ArgumentException(await GetRejectionReason(connection, part.Vendor_Id));

            return created;
        }

        // Called only after the insert was refused, which can only be an unknown or inactive vendor.
        private static async Task<string> GetRejectionReason(IDbConnection connection, long vendorId)
        {
            var vendorStatus = await connection.QuerySingleOrDefaultAsync<string?>(
                "SELECT LOWER(status) FROM vendor WHERE id = @Id", new { Id = vendorId });
            return vendorStatus is null
                ? "Vendor does not exist."
                : "Inactive vendors cannot be selected.";
        }
    }
}
