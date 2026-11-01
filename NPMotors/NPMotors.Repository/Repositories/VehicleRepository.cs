using NPMotors.Repository.Interface;
using System;
using System.Collections.Generic;
using System.Data;
using System.Text;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.Models.Enums;
using System.Threading.Tasks;
using Dapper;

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
