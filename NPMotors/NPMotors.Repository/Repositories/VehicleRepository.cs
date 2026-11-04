using NPMotors.Repository.Interface;
using System;
using System.Collections.Generic;
using System.Data;
using System.Text;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.Models.Enums;
using System.Threading.Tasks;
using Dapper;
using System.Linq;
using NPMotors.Repository.RequestFeatures;

namespace NPMotors.Repository.Repositories
{
    public class VehicleRepository : IVehicleRepository
    {
        private readonly IRepositoryContext _repositoryContext;

        public VehicleRepository(IRepositoryContext repositoryContext)
        {
            _repositoryContext = repositoryContext;
        }

        private const string VehicleColumns =
            "id, vendor_id, vin, make, model, year, color, purchase_date, warehouse, purchase_price, additional_costs, total_cost, selling_price, status";

        public async Task<bool> VinExists(string vin)
        {
            using IDbConnection connection = _repositoryContext.GetDatabaseConnection();

            return await connection.ExecuteScalarAsync<bool>(
                "SELECT EXISTS (SELECT 1 FROM vehicle WHERE LOWER(vin) = LOWER(@Vin))",
                new { Vin = vin.Trim() });
        }

        public async Task<VehicleResponseDto> AddVehicle(VehicleCreateDto vehicle)
        {
            using IDbConnection connection = _repositoryContext.GetDatabaseConnection();

            var queryParams = new DynamicParameters();
            queryParams.Add("VendorId", vehicle.Vendor_Id);
            queryParams.Add("Vin", vehicle.Vin.Trim());
            queryParams.Add("Make", vehicle.Make?.Trim());
            queryParams.Add("Model", vehicle.Model?.Trim());
            queryParams.Add("Year", vehicle.Year);
            queryParams.Add("Color", vehicle.Color?.Trim());
            queryParams.Add("PurchaseDate", vehicle.Purchase_Date?.Date, DbType.Date);
            queryParams.Add("Warehouse", vehicle.Warehouse?.Trim());
            queryParams.Add("PurchasePrice", vehicle.Purchase_Price);
            queryParams.Add("AdditionalCosts", vehicle.Additional_Costs ?? 0m);
            queryParams.Add("SellingPrice", vehicle.Selling_Price ?? 0m);
            queryParams.Add("Status", VehicleStatus.Available.ToDbValue());

            // total_cost = purchase_price + additional_costs; a new vehicle is always Available.
            return await connection.QuerySingleAsync<VehicleResponseDto>(
                $@"INSERT INTO vehicle (vendor_id, vin, make, model, year, color, purchase_date, warehouse,
                                        purchase_price, additional_costs, total_cost, selling_price, status)
                   VALUES (@VendorId, @Vin, @Make, @Model, @Year, @Color, @PurchaseDate, @Warehouse,
                           @PurchasePrice, @AdditionalCosts, @PurchasePrice + @AdditionalCosts, @SellingPrice, @Status)
                   RETURNING {VehicleColumns}",
                queryParams);
        }

        // Stored columns only; Total_Cost, Gross_Profit, Gross_Margin and Is_Available are filled by VehicleService.
        private const string BrowseColumns =
            "v.id, v.vendor_id, ve.name AS Vendor_Name, v.vin, v.make, v.model, v.year, v.color, v.purchase_date, v.warehouse, " +
            "COALESCE(v.purchase_price, 0) AS Purchase_Price, v.additional_costs, " +
            "COALESCE(v.selling_price, 0) AS Selling_Price, v.status";

        private const string BrowseFrom = "FROM vehicle v JOIN vendor ve ON ve.id = v.vendor_id";

        public async Task<PagedList<VehicleResponseDto>> GetAllVehicles(VehicleParameters parameters)
        {
            using IDbConnection connection = _repositoryContext.GetDatabaseConnection();

            var whereClauses = new List<string>();
            var queryParams = new DynamicParameters();

            var status = parameters.Status?.Trim();
            if (!string.IsNullOrWhiteSpace(status))
            {
                whereClauses.Add("LOWER(v.status) = @Status");
                queryParams.Add("Status", status.ToLowerInvariant());
            }

            var searchTerm = parameters.SearchTerm?.Trim();
            if (!string.IsNullOrWhiteSpace(searchTerm))
            {
                whereClauses.Add(@"(
                    LOWER(v.vin) LIKE @SearchTerm OR
                    LOWER(v.make) LIKE @SearchTerm OR
                    LOWER(v.model) LIKE @SearchTerm
                )");
                queryParams.Add("SearchTerm", $"%{searchTerm.ToLowerInvariant()}%");
            }

            var whereSql = whereClauses.Count > 0
                ? " WHERE " + string.Join(" AND ", whereClauses)
                : string.Empty;

            var count = await connection.ExecuteScalarAsync<int>($"SELECT COUNT(*) {BrowseFrom}{whereSql}", queryParams);

            queryParams.Add("Offset", (parameters.PageNumber - 1) * parameters.PageSize);
            queryParams.Add("Limit", parameters.PageSize);

            var vehicles = (await connection.QueryAsync<VehicleResponseDto>(
                $"SELECT {BrowseColumns} {BrowseFrom}{whereSql} ORDER BY v.make, v.model, v.id OFFSET @Offset LIMIT @Limit",
                queryParams)).ToList();

            return new PagedList<VehicleResponseDto>(vehicles, count, parameters.PageNumber, parameters.PageSize);
        }

        public async Task<VehicleResponseDto?> GetVehicleById(long id)
        {
            using IDbConnection connection = _repositoryContext.GetDatabaseConnection();

            return await connection.QuerySingleOrDefaultAsync<VehicleResponseDto>(
                $"SELECT {BrowseColumns} {BrowseFrom} WHERE v.id = @Id",
                new { Id = id });
        }

        public async Task<string?> GetVendorStatus(long vendorId)
        {
            using IDbConnection connection = _repositoryContext.GetDatabaseConnection();

            return await connection.QuerySingleOrDefaultAsync<string?>(
                "SELECT LOWER(status) FROM vendor WHERE id = @Id",
                new { Id = vendorId });
        }

        public async Task<bool> DeleteVehicle(long id)
        {
            using IDbConnection connection = _repositoryContext.GetDatabaseConnection();

            var rowsAffected = await connection.ExecuteAsync(
                "DELETE FROM vehicle WHERE id = @Id",
                new { Id = id });

            return rowsAffected > 0;
        }
    }
}
