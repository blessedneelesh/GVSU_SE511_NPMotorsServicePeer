using System.Data;
using Dapper;
using NPMotors.Repository.Interface;
using NPMotors.Repository.Models;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.Models.Enums;
using NPMotors.Repository.RequestFeatures;

namespace NPMotors.Repository.Repositories
{
    public class ArInvoiceRepository : IArInvoiceRepository
    {
        private readonly IRepositoryContext _context;

        public ArInvoiceRepository(IRepositoryContext context)
        {
            _context = context;
        }

        public async Task<long> InsertArInvoice(ArInvoice arInvoice, IDbTransaction tx)
        {
            var queryParams = new DynamicParameters();
            queryParams.Add("InvoiceId", arInvoice.InvoiceId);
            queryParams.Add("Date", arInvoice.Date.Date, DbType.Date);
            queryParams.Add("DueDate", arInvoice.DueDate?.Date, DbType.Date);
            queryParams.Add("Amount", arInvoice.Amount);
            queryParams.Add("Paid", arInvoice.Paid);
            queryParams.Add("Balance", arInvoice.Balance);
            queryParams.Add("Status", arInvoice.Status.ToDbValue());
            queryParams.Add("CreatedAt", arInvoice.CreatedAt);
            queryParams.Add("CreatedBy", arInvoice.CreatedBy);

            // The number is built from the row id ('AR-000001'), so the id is taken from the
            // identity sequence first and inserted explicitly.
            return await tx.Connection!.ExecuteScalarAsync<long>(
                @"WITH next_id AS (SELECT nextval(pg_get_serial_sequence('ar_invoice', 'id')) AS id)
                  INSERT INTO ar_invoice (id, invoice_id, number, date, due_date, amount, paid, balance, status, created_at, created_by)
                  SELECT n.id, @InvoiceId, 'AR-' || LPAD(n.id::text, 6, '0'), @Date, @DueDate, @Amount, @Paid, @Balance, @Status, @CreatedAt, @CreatedBy
                  FROM next_id n
                  RETURNING id",
                queryParams, tx);
        }

        // Stored columns plus the invoice number and customer name from the JOINs.
        public async Task<ArInvoice?> GetArInvoiceById(long id)
        {
            using IDbConnection connection = _context.GetDatabaseConnection();

            return await connection.QuerySingleOrDefaultAsync<ArInvoice>(
                @"SELECT ar.id, ar.invoice_id AS InvoiceId, ar.number, ar.date, ar.due_date AS DueDate,
                         ar.amount, ar.paid, ar.balance, ar.status, ar.created_at AS CreatedAt, ar.created_by AS CreatedBy,
                         si.number AS InvoiceNumber, CONCAT_WS(' ', c.first_name, c.last_name) AS CustomerName
                  FROM ar_invoice ar
                  JOIN sales_invoice si ON si.id = ar.invoice_id
                  JOIN customer c ON c.id = si.customer_id
                  WHERE ar.id = @Id",
                new { Id = id });
        }

        // Optimistic check: 0 rows means another payment changed the AR balance first.
        public async Task<int> UpdateArPayment(ArInvoice ar, decimal expectedBalance, IDbTransaction tx)
        {
            return await tx.Connection!.ExecuteAsync(
                @"UPDATE ar_invoice
                  SET paid = @Paid, balance = @Balance, status = @Status
                  WHERE id = @Id AND balance = @ExpectedBalance",
                new
                {
                    Id = ar.Id,
                    Paid = ar.Paid,
                    Balance = ar.Balance,
                    Status = ar.Status.ToDbValue(),
                    ExpectedBalance = expectedBalance
                },
                tx);
        }

        private const string AgingFrom =
            @"FROM ar_invoice ar
              JOIN sales_invoice si ON si.id = ar.invoice_id
              JOIN customer c ON c.id = si.customer_id";

        // Open AR only (balance > 0). Overdue = past due date; Unpaid/Partial = stored status and not past due.
        public async Task<PagedList<ArAgingDto>> GetArAging(ArAgingParameters parameters)
        {
            using IDbConnection connection = _context.GetDatabaseConnection();

            var asOfDate = (parameters.AsOfDate ?? DateTime.Today).Date;
            var whereClauses = new List<string> { "ar.balance > 0" };
            var queryParams = new DynamicParameters();
            queryParams.Add("AsOfDate", asOfDate, DbType.Date);

            if (parameters.CustomerId is not null)
            {
                whereClauses.Add("c.id = @CustomerId");
                queryParams.Add("CustomerId", parameters.CustomerId.Value);
            }

            var status = parameters.Status?.Trim().ToLowerInvariant();
            if (status == "overdue")
            {
                whereClauses.Add("ar.due_date < @AsOfDate");
            }
            else if (status == "unpaid" || status == "partial")
            {
                whereClauses.Add("LOWER(ar.status) = @Status");
                whereClauses.Add("(ar.due_date IS NULL OR ar.due_date >= @AsOfDate)");
                queryParams.Add("Status", status);
            }

            var whereSql = " WHERE " + string.Join(" AND ", whereClauses);

            var count = await connection.ExecuteScalarAsync<int>($"SELECT COUNT(*) {AgingFrom}{whereSql}", queryParams);

            queryParams.Add("Offset", (parameters.PageNumber - 1) * parameters.PageSize);
            queryParams.Add("Limit", parameters.PageSize);

            var items = (await connection.QueryAsync<ArAgingDto>(
                $@"SELECT ar.id AS Id, ar.number AS Number, c.id AS CustomerId,
                          CONCAT_WS(' ', c.first_name, c.last_name) AS Customer_Name,
                          si.number AS Invoice_Number, ar.due_date AS Due_Date,
                          ar.amount AS Amount, ar.paid AS Paid, ar.balance AS Balance, ar.status AS Status
                   {AgingFrom}{whereSql}
                   ORDER BY ar.due_date, ar.number OFFSET @Offset LIMIT @Limit",
                queryParams)).ToList();

            return new PagedList<ArAgingDto>(items, count, parameters.PageNumber, parameters.PageSize);
        }
    }
}
