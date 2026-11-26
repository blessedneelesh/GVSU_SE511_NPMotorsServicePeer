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
    }
}
