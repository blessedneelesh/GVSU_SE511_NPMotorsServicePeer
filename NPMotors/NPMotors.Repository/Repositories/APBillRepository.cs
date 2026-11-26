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

        private const string OpenBills = "ap.balance > 0 AND LOWER(ap.status) IN ('unpaid', 'partial')";

        // No status = every bill, paid included. "overdue" = open and past due date as of @AsOfDate;
        // "unpaid"/"partial" = stored status and not past due; "paid" = fully settled (balance <= 0).
        // Days_Overdue and Status are set by the service.
        public async Task<PagedList<ApBillResponseDto>> GetAllApBills(ApBillParameters parameters)
        {
            using IDbConnection connection = _context.GetDatabaseConnection();

            var asOfDate = (parameters.AsOfDate ?? DateTime.Today).Date;
            var status = parameters.Status?.Trim().ToLowerInvariant();
            var whereClauses = new List<string>();
            var queryParams = new DynamicParameters();
            queryParams.Add("AsOfDate", asOfDate, DbType.Date);

            if (parameters.VendorId is not null)
            {
                whereClauses.Add("ap.vendor_id = @VendorId");
                queryParams.Add("VendorId", parameters.VendorId.Value);
            }

            if (status == "overdue")
            {
                whereClauses.Add(OpenBills);
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
            const string from = "FROM ap_bill ap JOIN vendor v ON v.id = ap.vendor_id";

            var count = await connection.ExecuteScalarAsync<int>($"SELECT COUNT(*) {from}{whereSql}", queryParams);

            queryParams.Add("Offset", (parameters.PageNumber - 1) * parameters.PageSize);
            queryParams.Add("Limit", parameters.PageSize);

            // Vendor_Balance sums all of the vendor's open bills, whatever the filters or the page.
            var items = (await connection.QueryAsync<ApBillResponseDto>(
                $@"SELECT ap.id AS Id, ap.number AS Number, ap.vendor_id AS Vendor_Id, v.name AS Vendor_Name,
                          ap.date AS Date, ap.due_date AS Due_Date, ap.amount AS Amount, ap.paid AS Paid,
                          ap.balance AS Balance, ap.status AS Status,
                          CASE WHEN ap.due_date IS NULL OR @AsOfDate - ap.due_date <= 0 THEN 'Current'
                               WHEN @AsOfDate - ap.due_date <= 30 THEN '1-30'
                               WHEN @AsOfDate - ap.due_date <= 60 THEN '31-60'
                               WHEN @AsOfDate - ap.due_date <= 90 THEN '61-90'
                               ELSE 'Over 90' END AS Aging_Bucket,
                          (SELECT COALESCE(SUM(o.balance), 0) FROM ap_bill o
                           WHERE o.vendor_id = ap.vendor_id AND o.balance > 0
                             AND LOWER(o.status) IN ('unpaid', 'partial')) AS Vendor_Balance
                   {from}{whereSql}
                   ORDER BY ap.due_date, ap.number OFFSET @Offset LIMIT @Limit",
                queryParams)).ToList();

            return new PagedList<ApBillResponseDto>(items, count, parameters.PageNumber, parameters.PageSize);
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
