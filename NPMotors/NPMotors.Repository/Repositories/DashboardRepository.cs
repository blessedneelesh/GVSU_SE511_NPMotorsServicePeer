using System.Data;
using Dapper;
using NPMotors.Repository.Interface;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.Models.Enums;

namespace NPMotors.Repository.Repositories
{
    public class DashboardRepository : IDashboardRepository
    {
        private readonly IRepositoryContext _context;

        public DashboardRepository(IRepositoryContext context)
        {
            _context = context;
        }

        // Every vehicle, and those whose status is 'available' (case-insensitive).
        public async Task<InventoryCountsDto> GetInventoryCounts()
        {
            using IDbConnection connection = _context.GetDatabaseConnection();

            return await connection.QuerySingleAsync<InventoryCountsDto>(
                @"SELECT COUNT(*)::int AS Total_Inventory,
                         (COUNT(*) FILTER (WHERE LOWER(status) = @Available))::int AS Available_Vehicles
                  FROM vehicle",
                new { Available = VehicleStatus.Available.ToDbValue() });
        }

        // Available vehicles grouped by make ignoring case and surrounding spaces; blank or null makes are "Unknown".
        // The shown make is the alphabetically first spelling in the group. Count descending, then make.
        public async Task<List<MakeCountDto>> GetAvailableByMake()
        {
            using IDbConnection connection = _context.GetDatabaseConnection();

            var rows = await connection.QueryAsync<MakeCountDto>(
                @"SELECT MIN(COALESCE(NULLIF(BTRIM(make), ''), 'Unknown')) AS Make, COUNT(*)::int AS Count
                  FROM vehicle
                  WHERE LOWER(status) = @Available
                  GROUP BY LOWER(COALESCE(NULLIF(BTRIM(make), ''), 'Unknown'))
                  ORDER BY COUNT(*) DESC, 1",
                new { Available = VehicleStatus.Available.ToDbValue() });
            return rows.ToList();
        }

        // The same set GetAllAr returns with no Status: every AR row with its sales invoice and customer, paid included.
        public async Task<ReceivableTotalsDto> GetReceivableTotals()
        {
            using IDbConnection connection = _context.GetDatabaseConnection();

            return await connection.QuerySingleAsync<ReceivableTotalsDto>(
                @"SELECT COALESCE(SUM(ar.balance), 0) AS Receivable_Balance, COUNT(*)::int AS Open_Receivables
                  FROM ar_invoice ar
                  JOIN sales_invoice si ON si.id = ar.invoice_id
                  JOIN customer c ON c.id = si.customer_id");
        }

        public async Task<int> GetActiveCustomerCount()
        {
            using IDbConnection connection = _context.GetDatabaseConnection();

            return await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*)::int FROM customer WHERE LOWER(status) = @Active",
                new { Active = Status.Active.ToDbValue() });
        }

        // One row per calendar month from start's month to end's month (zeros when empty), oldest first.
        // Same filters as ReportRepository.GetSalesRevenue (posted invoices) and GetIncomeAdjustments
        // (posted incoming matched adjustments, not reversed), grouped by month in one query.
        public async Task<List<MonthlyRevenueComponentsDto>> GetMonthlyRevenueComponents(DateTime start, DateTime end)
        {
            using IDbConnection connection = _context.GetDatabaseConnection();

            var queryParams = new DynamicParameters();
            queryParams.Add("Start", start.Date, DbType.Date);
            queryParams.Add("End", end.Date, DbType.Date);

            var rows = await connection.QueryAsync<MonthlyRevenueComponentsDto>(
                @"WITH months AS (
                      SELECT generate_series(date_trunc('month', @Start::date), date_trunc('month', @End::date), interval '1 month')::date AS m
                  ),
                  sales AS (
                      SELECT date_trunc('month', date)::date AS m,
                             SUM(sale_price) AS vehicle_revenue, SUM(fees) AS fee_income, SUM(discount) AS discounts
                      FROM sales_invoice
                      WHERE lifecycle = 'posted' AND date BETWEEN @Start AND @End
                      GROUP BY 1
                  ),
                  adj AS (
                      SELECT date_trunc('month', t.date)::date AS m,
                             SUM(CASE WHEN l.adjustment_type = 'Interest Received' THEN t.amount ELSE 0 END) AS interest_income,
                             SUM(CASE WHEN l.adjustment_type = 'Other Adjustment' THEN t.amount ELSE 0 END) AS other_income
                      FROM ""transaction"" t
                      JOIN bank_statement_line l ON l.transaction_id = t.id
                      WHERE t.direction = 'in'
                        AND t.status = 'posted'
                        AND l.adjustment_type IS NOT NULL
                        AND l.match_status = 'Matched'
                        AND t.date BETWEEN @Start AND @End
                        AND NOT EXISTS (SELECT 1 FROM ""transaction"" r WHERE r.reverses_transaction_id = t.id)
                      GROUP BY 1
                  )
                  SELECT months.m AS Month,
                         COALESCE(sales.vehicle_revenue, 0) AS Vehicle_Revenue,
                         COALESCE(sales.fee_income, 0) AS Fee_Income,
                         COALESCE(sales.discounts, 0) AS Discounts,
                         COALESCE(adj.interest_income, 0) AS Interest_Income,
                         COALESCE(adj.other_income, 0) AS Other_Income
                  FROM months
                  LEFT JOIN sales ON sales.m = months.m
                  LEFT JOIN adj ON adj.m = months.m
                  ORDER BY months.m",
                queryParams);
            return rows.ToList();
        }
    }
}
