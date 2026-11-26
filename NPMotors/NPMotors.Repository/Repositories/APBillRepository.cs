using System.Data;
using Dapper;
using NPMotors.Repository.Interface;
using NPMotors.Repository.Models;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.RequestFeatures;

namespace NPMotors.Repository.Repositories
{
    public class APBillRepository : IAPBillRepository
    {
        private readonly IRepositoryContext _context;

        public APBillRepository(IRepositoryContext context)
        {
            _context = context;
        }

        private const string BillColumns =
            "id, number, vendor_id AS VendorId, vehicle_id AS VehicleId, date, due_date AS DueDate, amount, paid, balance, status";

        public async Task<APBill?> GetApBillById(long id, IDbTransaction? tx)
        {
            if (tx is not null)
            {
                // FOR UPDATE: a second payment on the same bill waits until this transaction ends.
                return await tx.Connection!.QuerySingleOrDefaultAsync<APBill>(
                    $"SELECT {BillColumns} FROM ap_bill WHERE id = @Id FOR UPDATE",
                    new { Id = id }, tx);
            }

            using IDbConnection connection = _context.GetDatabaseConnection();
            return await connection.QuerySingleOrDefaultAsync<APBill>(
                $"SELECT {BillColumns} FROM ap_bill WHERE id = @Id",
                new { Id = id });
        }

        // Paid, balance and status change together; amount and vehicle_id are never touched.
        public async Task<int> UpdateApBillPayment(APBill bill, IDbTransaction tx)
        {
            return await tx.Connection!.ExecuteAsync(
                "UPDATE ap_bill SET paid = @Paid, balance = @Balance, status = @Status WHERE id = @Id",
                new { bill.Id, bill.Paid, bill.Balance, bill.Status },
                tx);
        }

        public async Task<decimal> GetOpenBalanceByVendor(long vendorId, IDbTransaction tx)
        {
            return await tx.Connection!.ExecuteScalarAsync<decimal>(
                "SELECT COALESCE(SUM(balance), 0) FROM ap_bill WHERE vendor_id = @VendorId AND balance > 0",
                new { VendorId = vendorId },
                tx);
        }

        private const string AgingFrom =
            @"FROM ap_bill ap
              JOIN vendor v ON v.id = ap.vendor_id";

        // No status = every AP row, paid included. Overdue = open and past due date; Unpaid/Partial = stored status
        // and not past due; Paid = fully settled bill (balance <= 0).
        public async Task<PagedList<ApAgingDto>> GetAllAp(ApAgingParameters parameters)
        {
            using IDbConnection connection = _context.GetDatabaseConnection();

            var asOfDate = (parameters.AsOfDate ?? DateTime.Today).Date;
            var status = parameters.Status?.Trim().ToLowerInvariant();
            var whereClauses = new List<string>();
            var queryParams = new DynamicParameters();
            queryParams.Add("AsOfDate", asOfDate, DbType.Date);

            if (parameters.VendorId is not null)
            {
                whereClauses.Add("v.id = @VendorId");
                queryParams.Add("VendorId", parameters.VendorId.Value);
            }

            if (status == "overdue")
            {
                whereClauses.Add("ap.balance > 0");
                whereClauses.Add("ap.due_date < @AsOfDate");
            }
            else if (status == "paid")
            {
                whereClauses.Add("ap.balance <= 0");
            }
            else if (status == "unpaid" || status == "partial")
            {
                whereClauses.Add("LOWER(ap.status) = @Status");
                whereClauses.Add("(ap.due_date IS NULL OR ap.due_date >= @AsOfDate)");
                queryParams.Add("Status", status);
            }

            var whereSql = whereClauses.Count > 0 ? " WHERE " + string.Join(" AND ", whereClauses) : string.Empty;

            var count = await connection.ExecuteScalarAsync<int>($"SELECT COUNT(*) {AgingFrom}{whereSql}", queryParams);

            queryParams.Add("Offset", (parameters.PageNumber - 1) * parameters.PageSize);
            queryParams.Add("Limit", parameters.PageSize);

            var items = (await connection.QueryAsync<ApAgingDto>(
                $@"SELECT ap.id AS Id, ap.number AS Number, v.id AS VendorId, v.name AS Vendor_Name,
                          ap.due_date AS Due_Date, ap.amount AS Amount, ap.paid AS Paid,
                          ap.balance AS Balance, ap.status AS Status
                   {AgingFrom}{whereSql}
                   ORDER BY ap.due_date, ap.number OFFSET @Offset LIMIT @Limit",
                queryParams)).ToList();

            return new PagedList<ApAgingDto>(items, count, parameters.PageNumber, parameters.PageSize);
        }

        // One AP bill with its vendor (name, contact, phone, email, address); null when not found.
        public async Task<ApDetailDto?> GetApById(long id)
        {
            using IDbConnection connection = _context.GetDatabaseConnection();

            return await connection.QuerySingleOrDefaultAsync<ApDetailDto>(
                @"SELECT ap.id AS Id, ap.number AS Number, ap.vendor_id AS VendorId, v.name AS Vendor,
                         ap.vehicle_id AS VehicleId, ap.date AS Date, ap.due_date AS DueDate,
                         ap.amount AS Amount, ap.paid AS Paid, ap.balance AS Balance, ap.status AS Status,
                         v.contact AS Contact, v.phone AS Phone, v.email AS Email, v.address AS Address
                  FROM ap_bill ap
                  JOIN vendor v ON v.id = ap.vendor_id
                  WHERE ap.id = @Id",
                new { Id = id });
        }
    }
}
