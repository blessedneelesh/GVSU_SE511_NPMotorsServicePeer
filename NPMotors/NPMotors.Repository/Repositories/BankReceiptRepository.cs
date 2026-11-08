using System.Data;
using Dapper;
using NPMotors.Repository.Interface;
using NPMotors.Repository.Models;

namespace NPMotors.Repository.Repositories
{
    public class BankReceiptRepository : IBankReceiptRepository
    {
        private readonly IRepositoryContext _context;

        public BankReceiptRepository(IRepositoryContext context)
        {
            _context = context;
        }

        public async Task<long> InsertBankReceipt(BankReceipt receipt, IDbTransaction tx)
        {
            var queryParams = new DynamicParameters();
            queryParams.Add("TransactionId", receipt.TransactionId);
            queryParams.Add("BankAccountId", receipt.BankAccountId);
            queryParams.Add("CustomerId", receipt.CustomerId);
            queryParams.Add("SalesInvoiceId", receipt.SalesInvoiceId);
            queryParams.Add("ArInvoiceId", receipt.ArInvoiceId);
            queryParams.Add("NumberPrefix", $"RCT-{receipt.CreatedAt.Year}-");
            queryParams.Add("ReceiptDate", receipt.ReceiptDate.Date, DbType.Date);
            queryParams.Add("PaymentMethod", receipt.PaymentMethod.ToString());
            queryParams.Add("Reference", receipt.Reference);
            queryParams.Add("Amount", receipt.Amount);
            queryParams.Add("Status", receipt.Status);
            queryParams.Add("CreatedAt", receipt.CreatedAt);
            queryParams.Add("CreatedBy", receipt.CreatedBy);

            // The receipt number is built from the row id (RCT-<year>-<id padded to 6>).
            return await tx.Connection!.ExecuteScalarAsync<long>(
                @"WITH next_id AS (SELECT nextval(pg_get_serial_sequence('bank_receipt', 'id')) AS id)
                  INSERT INTO bank_receipt (id, transaction_id, bank_account_id, customer_id, ar_invoice_id, sales_invoice_id,
                                            receipt_number, receipt_date, payment_method, reference, amount, status, created_at, created_by)
                  SELECT n.id, @TransactionId, @BankAccountId, @CustomerId, @ArInvoiceId, @SalesInvoiceId,
                         @NumberPrefix || LPAD(n.id::text, 6, '0'), @ReceiptDate, @PaymentMethod, @Reference, @Amount, @Status, @CreatedAt, @CreatedBy
                  FROM next_id n
                  RETURNING id",
                queryParams, tx);
        }

        // Oldest first; receipts are only ever inserted, so this is the full payment history.
        public async Task<IEnumerable<BankReceipt>> GetReceiptsByArInvoiceId(long arInvoiceId)
        {
            using IDbConnection connection = _context.GetDatabaseConnection();

            return await connection.QueryAsync<BankReceipt>(
                @"SELECT br.id, br.transaction_id AS TransactionId, br.bank_account_id AS BankAccountId,
                         br.customer_id AS CustomerId, br.sales_invoice_id AS SalesInvoiceId, br.ar_invoice_id AS ArInvoiceId,
                         br.receipt_number AS ReceiptNumber, br.receipt_date AS ReceiptDate, br.payment_method AS PaymentMethod,
                         br.reference, br.amount, br.status, br.created_at AS CreatedAt, br.created_by AS CreatedBy,
                         ba.name AS BankAccountName
                  FROM bank_receipt br
                  JOIN bank_account ba ON ba.id = br.bank_account_id
                  WHERE br.ar_invoice_id = @ArInvoiceId
                  ORDER BY br.receipt_date, br.id",
                new { ArInvoiceId = arInvoiceId });
        }
    }
}
