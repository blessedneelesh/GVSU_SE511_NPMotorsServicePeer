using System.Data;
using Dapper;
using NPMotors.Repository.Interface;
using NPMotors.Repository.Models;

namespace NPMotors.Repository.Repositories
{
    public class BankPaymentRepository : IBankPaymentRepository
    {
        private readonly IRepositoryContext _context;

        private const string CreatedBy = "system";

        public BankPaymentRepository(IRepositoryContext context)
        {
            _context = context;
        }

        // Always a new row: earlier payments are never updated. The payment number is built from the
        // row id (PAY-<year>-<id padded to 6>).
        public async Task<long> InsertBankPayment(BankPayment payment, IDbTransaction tx)
        {
            var queryParams = new DynamicParameters();
            queryParams.Add("TransactionId", payment.TransactionId);
            queryParams.Add("BankAccountId", payment.BankAccountId);
            queryParams.Add("VendorId", payment.VendorId);
            queryParams.Add("ApBillId", payment.ApBillId);
            queryParams.Add("VehicleId", payment.VehicleId);
            queryParams.Add("NumberPrefix", $"PAY-{DateTime.UtcNow.Year}-");
            queryParams.Add("PaymentDate", payment.PaymentDate.Date, DbType.Date);
            queryParams.Add("PaymentMethod", payment.PaymentMethod);
            queryParams.Add("Reference", payment.Reference);
            queryParams.Add("Amount", payment.Amount);
            queryParams.Add("Status", payment.Status);
            queryParams.Add("CreatedBy", CreatedBy);

            return await tx.Connection!.ExecuteScalarAsync<long>(
                @"WITH next_id AS (SELECT nextval(pg_get_serial_sequence('bank_payment', 'id')) AS id)
                  INSERT INTO bank_payment (id, transaction_id, bank_account_id, vendor_id, ap_bill_id, vehicle_id,
                                            payment_number, payment_date, payment_method, reference, amount, status, created_by)
                  SELECT n.id, @TransactionId, @BankAccountId, @VendorId, @ApBillId, @VehicleId,
                         @NumberPrefix || LPAD(n.id::text, 6, '0'), @PaymentDate, @PaymentMethod, @Reference, @Amount, @Status, @CreatedBy
                  FROM next_id n
                  RETURNING id",
                queryParams, tx);
        }

        // Oldest first; payments are only ever inserted, so this is the full payment history.
        public async Task<IEnumerable<BankPayment>> GetPaymentsByBillId(long apBillId)
        {
            using IDbConnection connection = _context.GetDatabaseConnection();

            return await connection.QueryAsync<BankPayment>(
                @"SELECT id, transaction_id AS TransactionId, bank_account_id AS BankAccountId, vendor_id AS VendorId,
                         ap_bill_id AS ApBillId, vehicle_id AS VehicleId, payment_number AS PaymentNumber,
                         payment_date AS PaymentDate, payment_method AS PaymentMethod, reference, amount, status
                  FROM bank_payment
                  WHERE ap_bill_id = @ApBillId
                  ORDER BY payment_date, payment_number",
                new { ApBillId = apBillId });
        }
    }
}
