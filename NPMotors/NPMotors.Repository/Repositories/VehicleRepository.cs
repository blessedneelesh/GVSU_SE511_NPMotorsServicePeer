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
using NPMotors.Repository.Models;
using NPMotors.Services.Abstractions.ServiceInterfaces;
using Npgsql;

namespace NPMotors.Repository.Repositories
{
    public class VehicleRepository : IVehicleRepository
    {
        private readonly IRepositoryContext _repositoryContext;
        private readonly ITransactionPostingService _transactionPostingService;

        // No user context exists yet, so new records are attributed to the system.
        private const string CreatedBy = "system";
        private const string PaymentStatus = "Posted";

        public VehicleRepository(IRepositoryContext repositoryContext, ITransactionPostingService transactionPostingService)
        {
            _repositoryContext = repositoryContext;
            _transactionPostingService = transactionPostingService;
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
            // No user context exists yet, so new vehicles are attributed to the system.
            queryParams.Add("CreatedBy", "system");

            // total_cost = purchase_price + additional_costs; a new vehicle is always Available.
            return await connection.QuerySingleAsync<VehicleResponseDto>(
                $@"INSERT INTO vehicle (vendor_id, vin, make, model, year, color, purchase_date, warehouse,
                                        purchase_price, additional_costs, total_cost, selling_price, status, created_by)
                   VALUES (@VendorId, @Vin, @Make, @Model, @Year, @Color, @PurchaseDate, @Warehouse,
                           @PurchasePrice, @AdditionalCosts, @PurchasePrice + @AdditionalCosts, @SellingPrice, @Status, @CreatedBy)
                   RETURNING {VehicleColumns}",
                queryParams);
        }

        // One database transaction: the vehicle, the Out transaction (posted by Stories 8-9, which lowers the
        // book balance and writes the statement line), the AP Bill (Partial / Credit) and the bank payment
        // save together, or none do. total_cost is purchase_price + additional_costs and never changes afterwards.
        public async Task<VehiclePurchaseResponseDto> PurchaseVehicle(VehiclePurchaseCreateDto purchase, APBill? apBill)
        {
            using IDbConnection connection = _repositoryContext.GetDatabaseConnection();
            if (connection.State != ConnectionState.Open)
                connection.Open();
            using IDbTransaction tx = connection.BeginTransaction();

            try
            {
                var queryParams = new DynamicParameters();
                queryParams.Add("VendorId", purchase.Vendor_Id);
                queryParams.Add("Vin", purchase.Vin.Trim());
                queryParams.Add("Make", purchase.Make?.Trim());
                queryParams.Add("Model", purchase.Model?.Trim());
                queryParams.Add("Year", purchase.Year);
                queryParams.Add("Color", purchase.Color?.Trim());
                queryParams.Add("PurchaseDate", purchase.Purchase_Date?.Date, DbType.Date);
                queryParams.Add("Warehouse", purchase.Warehouse?.Trim());
                queryParams.Add("PurchasePrice", purchase.Purchase_Price);
                queryParams.Add("AdditionalCosts", purchase.Additional_Costs ?? 0m);
                queryParams.Add("SellingPrice", purchase.Selling_Price ?? 0m);
                queryParams.Add("Status", VehicleStatus.Available.ToDbValue());
                queryParams.Add("CreatedBy", CreatedBy);

                // 1. The vehicle, always Available.
                VehiclePurchaseResponseDto result;
                try
                {
                    result = await connection.QuerySingleAsync<VehiclePurchaseResponseDto>(
                        $@"INSERT INTO vehicle (vendor_id, vin, make, model, year, color, purchase_date, warehouse,
                                                purchase_price, additional_costs, total_cost, selling_price, status, created_by)
                           VALUES (@VendorId, @Vin, @Make, @Model, @Year, @Color, @PurchaseDate, @Warehouse,
                                   @PurchasePrice, @AdditionalCosts, @PurchasePrice + @AdditionalCosts, @SellingPrice, @Status, @CreatedBy)
                           RETURNING {VehicleColumns}",
                        queryParams, tx);
                }
                catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
                {
                    throw new ArgumentException("A vehicle with this VIN already exists.");
                }

                var vendorName = await connection.QuerySingleAsync<string>(
                    "SELECT name FROM vendor WHERE id = @Id", new { Id = purchase.Vendor_Id }, tx);
                result.Vendor_Name = vendorName;
                result.Payment_Condition = purchase.Payment_Condition;
                result.Amount_Paid = purchase.Amount_Paid;

                var paymentDate = (purchase.Purchase_Date ?? DateTime.Today).Date;

                // The payment number is built from the bank_payment id, which is reserved first so the
                // Transaction can carry it as its source number.
                long? paymentId = null;
                string? paymentNumber = null;
                if (purchase.Amount_Paid > 0)
                {
                    paymentId = await connection.ExecuteScalarAsync<long>(
                        "SELECT nextval(pg_get_serial_sequence('bank_payment', 'id'))", transaction: tx);
                    paymentNumber = $"PAY-{DateTime.UtcNow.Year}-{paymentId:D6}";

                    // 2. The Out transaction. It locks the bank account, checks it is active and that the amount
                    //    does not exceed its book balance, lowers the balance and inserts the statement line.
                    var bankAccountId = purchase.Bank_Account_Id!.Value;
                    var transactionId = await _transactionPostingService.PostTransaction(new PostTransactionRequest
                    {
                        BankAccountId = bankAccountId,
                        Direction = Direction.Out,
                        Amount = purchase.Amount_Paid,
                        Date = paymentDate,
                        Type = TransactionType.BankPayment,
                        SourceNumber = paymentNumber,
                        Party = vendorName,
                        Description = $"Vehicle purchase {result.Vin}",
                        Status = TransactionStatus.Posted
                    }, tx);

                    result.Transaction_Id = transactionId;
                    result.Book_Balance = await connection.ExecuteScalarAsync<decimal>(
                        "SELECT book_balance FROM bank_account WHERE id = @Id", new { Id = bankAccountId }, tx);
                }

                // 3. The AP Bill, only when the service prepared one (Partial or Credit).
                if (apBill is not null)
                {
                    var apParams = new DynamicParameters();
                    apParams.Add("VendorId", purchase.Vendor_Id);
                    apParams.Add("VehicleId", result.Id);
                    apParams.Add("NumberPrefix", $"AP-{DateTime.UtcNow.Year}-");
                    apParams.Add("Date", apBill.Date.Date, DbType.Date);
                    apParams.Add("DueDate", apBill.DueDate?.Date, DbType.Date);
                    apParams.Add("Amount", apBill.Amount);
                    apParams.Add("Paid", apBill.Paid);
                    apParams.Add("Balance", apBill.Balance);
                    apParams.Add("Status", apBill.Status);
                    apParams.Add("CreatedBy", CreatedBy);

                    var ap = await connection.QuerySingleAsync<ApBillKey>(
                        @"WITH next_id AS (SELECT nextval(pg_get_serial_sequence('ap_bill', 'id')) AS id)
                          INSERT INTO ap_bill (id, number, vendor_id, vehicle_id, date, due_date, amount, paid, balance, status, created_by)
                          SELECT n.id, @NumberPrefix || LPAD(n.id::text, 6, '0'), @VendorId, @VehicleId, @Date, @DueDate,
                                 @Amount, @Paid, @Balance, @Status, @CreatedBy
                          FROM next_id n
                          RETURNING id, number",
                        apParams, tx);
                    result.Ap_Bill_Id = ap.Id;
                    result.Ap_Bill_Number = ap.Number;
                }

                // 4. The bank payment, linked to the vehicle and to the AP Bill when one exists.
                if (paymentId is not null)
                {
                    await connection.ExecuteAsync(
                        @"INSERT INTO bank_payment (id, transaction_id, bank_account_id, vendor_id, ap_bill_id, vehicle_id, payment_number,
                                                    payment_date, payment_method, reference, amount, description, status, created_by)
                          VALUES (@Id, @TransactionId, @BankAccountId, @VendorId, @ApBillId, @VehicleId, @PaymentNumber,
                                  @PaymentDate, @PaymentMethod, @Reference, @Amount, @Description, @Status, @CreatedBy)",
                        new
                        {
                            Id = paymentId.Value,
                            TransactionId = result.Transaction_Id,
                            BankAccountId = purchase.Bank_Account_Id,
                            VendorId = purchase.Vendor_Id,
                            ApBillId = result.Ap_Bill_Id,
                            VehicleId = result.Id,
                            PaymentNumber = paymentNumber,
                            PaymentDate = paymentDate,
                            PaymentMethod = Enum.Parse<PaymentMethod>(purchase.Payment_Method!, true).ToString(),
                            Reference = purchase.Reference?.Trim(),
                            Amount = purchase.Amount_Paid,
                            Description = $"Vehicle purchase {result.Vin}",
                            Status = PaymentStatus,
                            CreatedBy
                        }, tx);
                    result.Bank_Payment_Id = paymentId;
                    result.Payment_Number = paymentNumber;
                }

                // 5. Everything saved together.
                tx.Commit();
                return result;
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }

        private sealed class ApBillKey
        {
            public long Id { get; set; }
            public string Number { get; set; } = string.Empty;
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

        // Marks the vehicle Sold and links it to the invoice, only while it is still Available.
        // 0 rows means it was sold in the meantime.
        public async Task<int> MarkVehicleSold(long vehicleId, long invoiceId, IDbTransaction tx)
        {
            return await tx.Connection!.ExecuteAsync(
                @"UPDATE vehicle SET status = @Sold, invoice_id = @InvoiceId
                  WHERE id = @VehicleId AND LOWER(status) = @Available",
                new
                {
                    VehicleId = vehicleId,
                    InvoiceId = invoiceId,
                    Sold = VehicleStatus.Sold.ToDbValue(),
                    Available = VehicleStatus.Available.ToDbValue()
                },
                tx);
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
