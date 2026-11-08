using System.Data;
using Dapper;
using NPMotors.Repository.Interface;
using NPMotors.Repository.Models;
using NPMotors.Repository.Models.Enums;

namespace NPMotors.Repository.Repositories
{
    public class ArInvoiceRepository : IArInvoiceRepository
    {
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
    }
}
