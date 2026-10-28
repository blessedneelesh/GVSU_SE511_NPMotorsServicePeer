using NPMotors.Repository.Interface;
using System;
using System.Collections.Generic;
using System.Data;
using System.Text;
using NPMotors.Repository.Models.Dto;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using NPMotors.Repository.RequestFeatures;

namespace NPMotors.Repository.Repositories
{
    public class VendorRepository : IVendorRepository
    {
        private readonly IRepositoryContext _repositoryContext;

        public VendorRepository(IRepositoryContext repositoryContext)
        {
            _repositoryContext = repositoryContext;
        }

        private static readonly string[] AllowedTypes = { "vehicle", "parts", "service" };

        private const string VendorColumns =
            "id, name, contact, phone, email, address, type, balance, total_purchases AS TotalPurchases, status";

        public async Task<PagedList<VendorResponseDto>> GetAllVendors(VendorParameters vendorParameters)
        {
            using IDbConnection connection = _repositoryContext.GetDatabaseConnection();

            var whereClauses = new List<string>();
            var queryParams = new DynamicParameters();

            var status = vendorParameters.Status?.Trim();
            if (!string.IsNullOrWhiteSpace(status) &&
                (status.Equals("active", StringComparison.OrdinalIgnoreCase) ||
                 status.Equals("inactive", StringComparison.OrdinalIgnoreCase)))
            {
                whereClauses.Add("LOWER(status) = @Status");
                queryParams.Add("Status", status.ToLowerInvariant());
            }

            var type = vendorParameters.Type?.Trim();
            if (!string.IsNullOrWhiteSpace(type) &&
                AllowedTypes.Contains(type, StringComparer.OrdinalIgnoreCase))
            {
                whereClauses.Add("LOWER(type) = @Type");
                queryParams.Add("Type", type.ToLowerInvariant());
            }

            var searchTerm = vendorParameters.SearchTerm?.Trim();
            if (!string.IsNullOrWhiteSpace(searchTerm))
            {
                whereClauses.Add(@"(
                    LOWER(name) LIKE @SearchTerm OR
                    LOWER(contact) LIKE @SearchTerm OR
                    LOWER(email) LIKE @SearchTerm OR
                    LOWER(phone) LIKE @SearchTerm OR
                    LOWER(address) LIKE @SearchTerm
                )");
                queryParams.Add("SearchTerm", $"%{searchTerm.ToLowerInvariant()}%");
            }

            var whereSql = whereClauses.Count > 0
                ? " WHERE " + string.Join(" AND ", whereClauses)
                : string.Empty;

            var count = await connection.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM vendor{whereSql}", queryParams);

            queryParams.Add("Offset", (vendorParameters.PageNumber - 1) * vendorParameters.PageSize);
            queryParams.Add("Limit", vendorParameters.PageSize);

            var vendors = (await connection.QueryAsync<VendorResponseDto>(
                $"SELECT {VendorColumns} FROM vendor{whereSql} ORDER BY name, id OFFSET @Offset LIMIT @Limit",
                queryParams)).ToList();

            return new PagedList<VendorResponseDto>(
                vendors,
                count,
                vendorParameters.PageNumber,
                vendorParameters.PageSize);
        }

        public async Task<VendorResponseDto?> GetVendorById(long id)
        {
            using IDbConnection connection = _repositoryContext.GetDatabaseConnection();

            return await connection.QuerySingleOrDefaultAsync<VendorResponseDto>(
                $"SELECT {VendorColumns} FROM vendor WHERE id = @Id",
                new { Id = id });
        }

        public async Task<IEnumerable<VendorResponseDto>> GetActiveVendors(string? type = null)
        {
            using IDbConnection connection = _repositoryContext.GetDatabaseConnection();

            var whereSql = "LOWER(status) = 'active'";
            var queryParams = new DynamicParameters();

            var trimmedType = type?.Trim();
            if (!string.IsNullOrWhiteSpace(trimmedType) &&
                AllowedTypes.Contains(trimmedType, StringComparer.OrdinalIgnoreCase))
            {
                whereSql += " AND LOWER(type) = @Type";
                queryParams.Add("Type", trimmedType.ToLowerInvariant());
            }

            return await connection.QueryAsync<VendorResponseDto>(
                $"SELECT {VendorColumns} FROM vendor WHERE {whereSql} ORDER BY name, id",
                queryParams);
        }

        public async Task<VendorResponseDto> AddVendor(VendorCreateDto vendor)
        {
            using IDbConnection connection = _repositoryContext.GetDatabaseConnection();

            var queryParams = new DynamicParameters();
            queryParams.Add("Name", vendor.Name.Trim());
            queryParams.Add("Contact", vendor.Contact?.Trim());
            queryParams.Add("Phone", vendor.Phone?.Trim());
            queryParams.Add("Email", vendor.Email?.Trim());
            queryParams.Add("Address", vendor.Address?.Trim());
            queryParams.Add("Type", vendor.Type.Trim());
            queryParams.Add("Status", (vendor.Status ?? "active").Trim().ToLowerInvariant());

            // balance and total_purchases are not written; the column defaults (0) apply.
            return await connection.QuerySingleAsync<VendorResponseDto>(
                $@"INSERT INTO vendor (name, contact, phone, email, address, type, status)
                   VALUES (@Name, @Contact, @Phone, @Email, @Address, @Type, @Status)
                   RETURNING {VendorColumns}",
                queryParams);
        }

        public async Task<VendorResponseDto?> UpdateVendor(long id, VendorUpdateDto vendor)
        {
            using IDbConnection connection = _repositoryContext.GetDatabaseConnection();

            var queryParams = new DynamicParameters();
            queryParams.Add("Id", id);
            queryParams.Add("Name", vendor.Name.Trim());
            queryParams.Add("Contact", vendor.Contact?.Trim());
            queryParams.Add("Phone", vendor.Phone?.Trim());
            queryParams.Add("Email", vendor.Email?.Trim());
            queryParams.Add("Address", vendor.Address?.Trim());
            queryParams.Add("Type", vendor.Type.Trim());
            queryParams.Add("Status", (vendor.Status ?? "active").Trim().ToLowerInvariant());

            return await connection.QuerySingleOrDefaultAsync<VendorResponseDto>(
                $@"UPDATE vendor
                   SET name = @Name, contact = @Contact, phone = @Phone, email = @Email,
                       address = @Address, type = @Type, status = @Status
                   WHERE id = @Id
                   RETURNING {VendorColumns}",
                queryParams);
        }

        public async Task<bool> DeleteVendor(long id)
        {
            using IDbConnection connection = _repositoryContext.GetDatabaseConnection();

            var rowsAffected = await connection.ExecuteAsync(
                "DELETE FROM vendor WHERE id = @Id",
                new { Id = id });

            return rowsAffected > 0;
        }

        public async Task<bool> IsVendorReferenced(long id)
        {
            using IDbConnection connection = _repositoryContext.GetDatabaseConnection();

            return await connection.ExecuteScalarAsync<bool>(
                @"SELECT EXISTS (SELECT 1 FROM part WHERE vendor_id = @Id)
                      OR EXISTS (SELECT 1 FROM vehicle WHERE vendor_id = @Id)
                      OR EXISTS (SELECT 1 FROM ap_bill WHERE vendor_id = @Id)
                      OR EXISTS (SELECT 1 FROM bank_payment WHERE vendor_id = @Id)",
                new { Id = id });
        }
    }
}
