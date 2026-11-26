using System.Data;
using Dapper;
using NPMotors.Repository.Interface;
using NPMotors.Repository.Models.Dto;

namespace NPMotors.Repository.Repositories
{
    public class ReportRepository : IReportRepository
    {
        private readonly IRepositoryContext _context;

        public ReportRepository(IRepositoryContext context)
        {
            _context = context;
        }

        // Posted sales invoices only (drafts and cancelled excluded); payment status is ignored.
        // Tax is never selected. Service_Revenue is 0 until service records exist.
        public async Task<SalesRevenueTotalsDto> GetSalesRevenue(DateTime start, DateTime end)
        {
            using IDbConnection connection = _context.GetDatabaseConnection();

            return await connection.QuerySingleAsync<SalesRevenueTotalsDto>(
                @"SELECT COALESCE(SUM(sale_price), 0) AS Vehicle_Revenue,
                         0::numeric AS Service_Revenue,
                         COALESCE(SUM(fees), 0) AS Fee_Income,
                         COALESCE(SUM(discount), 0) AS Discounts,
                         COUNT(*)::int AS Invoice_Count
                  FROM sales_invoice
                  WHERE lifecycle = 'posted'
                    AND date BETWEEN @Start AND @End",
                new { Start = start.Date, End = end.Date });
        }

        // Posted incoming adjustments matched to a transaction and not reversed.
        // The JOIN drops statement-only lines (transaction_id NULL, Unmatched).
        public async Task<List<IncomeAdjustmentDto>> GetIncomeAdjustments(DateTime start, DateTime end)
        {
            using IDbConnection connection = _context.GetDatabaseConnection();

            var rows = await connection.QueryAsync<IncomeAdjustmentDto>(
                @"SELECT t.id AS Transaction_Id, t.number AS Number, t.date::timestamp AS Date,
                         t.amount AS Amount, l.adjustment_type AS Adjustment_Type
                  FROM ""transaction"" t
                  JOIN bank_statement_line l ON l.transaction_id = t.id
                  WHERE t.direction = 'in'
                    AND t.status = 'posted'
                    AND l.adjustment_type IS NOT NULL
                    AND l.match_status = 'Matched'
                    AND t.date BETWEEN @Start AND @End
                    AND NOT EXISTS (SELECT 1 FROM ""transaction"" r WHERE r.reverses_transaction_id = t.id)
                  ORDER BY t.date, t.id",
                new { Start = start.Date, End = end.Date });
            return rows.ToList();
        }

        // Vehicles on posted invoices only: unsold stock and cancelled sales are not returned, and AP tables are not read.
        // uq_sales_invoice_vehicle_posted allows one posted invoice per vehicle, so each vehicle appears once.
        public async Task<List<SoldVehicleCostDto>> GetSoldVehicleCosts(DateTime start, DateTime end)
        {
            using IDbConnection connection = _context.GetDatabaseConnection();

            var rows = await connection.QueryAsync<SoldVehicleCostDto>(
                @"SELECT v.id AS Vehicle_Id, v.vin AS Vin, si.id AS Invoice_Id, si.number AS Invoice_Number,
                         si.date::timestamp AS Invoice_Date, COALESCE(v.total_cost, 0) AS Total_Cost
                  FROM sales_invoice si
                  JOIN vehicle v ON v.id = si.vehicle_id
                  WHERE si.lifecycle = 'posted'
                    AND si.date BETWEEN @Start AND @End
                  ORDER BY si.date, si.id",
                new { Start = start.Date, End = end.Date });
            return rows.ToList();
        }

        // Active employees with annual salary and bonus, the commission rate (0-100) and the sale prices
        // of their posted invoices in the period (0 when none).
        public async Task<List<EmployeePayrollDto>> GetEmployeePayroll(DateTime start, DateTime end)
        {
            using IDbConnection connection = _context.GetDatabaseConnection();

            var rows = await connection.QueryAsync<EmployeePayrollDto>(
                @"SELECT e.id AS Employee_Id, CONCAT_WS(' ', e.first_name, e.last_name) AS Employee_Name,
                         COALESCE(e.salary, 0) AS Salary, COALESCE(e.bonus, 0) AS Bonus,
                         COALESCE(e.commission, 0) AS Commission_Rate,
                         COALESCE((SELECT SUM(si.sale_price)
                                   FROM sales_invoice si
                                   WHERE si.salesperson_id = e.id
                                     AND si.lifecycle = 'posted'
                                     AND si.date BETWEEN @Start AND @End), 0) AS Commission_Sales
                  FROM employee e
                  WHERE LOWER(e.status) = 'active'
                  ORDER BY e.id",
                new { Start = start.Date, End = end.Date });
            return rows.ToList();
        }

        // Posted bank payments plus posted outgoing matched adjustments (UNION ALL). Is_Reversed = a reversal
        // exists for the transaction. The JOIN drops statement-only lines. Transfers are neither payments nor
        // adjustments, so they are never read.
        public async Task<List<ExpenseCandidateDto>> GetExpenseCandidates(DateTime start, DateTime end)
        {
            using IDbConnection connection = _context.GetDatabaseConnection();

            var rows = await connection.QueryAsync<ExpenseCandidateDto>(
                @"SELECT 'Payment' AS Source_Type, bp.id AS Source_Id, bp.payment_number AS Source_Number,
                         bp.payment_date::timestamp AS Date, bp.amount AS Amount,
                         bp.payment_category AS Payment_Category, NULL::varchar AS Adjustment_Type,
                         bp.ap_bill_id AS Ap_Bill_Id, bp.vehicle_id AS Vehicle_Id,
                         EXISTS (SELECT 1 FROM ""transaction"" r WHERE r.reverses_transaction_id = t.id) AS Is_Reversed
                  FROM bank_payment bp
                  JOIN ""transaction"" t ON t.id = bp.transaction_id
                  WHERE bp.status = 'Posted'
                    AND t.status = 'posted'
                    AND bp.payment_date BETWEEN @Start AND @End
                  UNION ALL
                  SELECT 'Adjustment' AS Source_Type, t.id AS Source_Id, t.number AS Source_Number,
                         t.date::timestamp AS Date, t.amount AS Amount,
                         NULL::varchar AS Payment_Category, l.adjustment_type AS Adjustment_Type,
                         NULL::bigint AS Ap_Bill_Id, NULL::bigint AS Vehicle_Id,
                         EXISTS (SELECT 1 FROM ""transaction"" r WHERE r.reverses_transaction_id = t.id) AS Is_Reversed
                  FROM ""transaction"" t
                  JOIN bank_statement_line l ON l.transaction_id = t.id
                  WHERE t.direction = 'out'
                    AND t.status = 'posted'
                    AND l.adjustment_type IS NOT NULL
                    AND l.match_status = 'Matched'
                    AND t.date BETWEEN @Start AND @End
                  ORDER BY 4, 1, 2",
                new { Start = start.Date, End = end.Date });
            return rows.ToList();
        }
    }
}
