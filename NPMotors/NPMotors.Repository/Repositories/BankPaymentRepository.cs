using System.ComponentModel.DataAnnotations;
using System.Data;
using Dapper;
using NPMotors.Repository.Interface;
using NPMotors.Repository.Models;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.Models.Enums;

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

        private const string PostedStatus = "Posted";

        private class AccountRow
        {
            public decimal OpeningBalance { get; set; }
            public decimal BookBalance { get; set; }
            public string Status { get; set; } = string.Empty;
        }

        // One database transaction; any error rolls everything back.
        public async Task<BankPaymentResponseDto> RecordMiscellaneousPayment(MiscellaneousPaymentCreateDto payment)
        {
            var method = ParsePaymentMethod(payment.Payment_Method);
            var payee = payment.Payee.Trim();
            var category = payment.Payment_Category.Trim();
            var reference = payment.Reference?.Trim();
            var description = payment.Description?.Trim();
            var date = payment.Payment_Date!.Value.Date;

            using IDbConnection connection = _context.GetDatabaseConnection();
            if (connection.State != ConnectionState.Open)
                connection.Open();
            using IDbTransaction tx = connection.BeginTransaction();

            try
            {
                // 1. Lock the bank account: it must exist, be active and cover the amount.
                var account = await connection.QuerySingleOrDefaultAsync<AccountRow>(
                    @"SELECT opening_balance AS OpeningBalance, book_balance AS BookBalance, LOWER(status) AS Status
                      FROM bank_account WHERE id = @Id FOR UPDATE",
                    new { Id = payment.Bank_Account_Id }, tx);
                if (account is null)
                    throw new ValidationException("An existing bank account is required.");
                if (account.Status != "active")
                    throw new ValidationException("The bank account must be active.");
                if (payment.Amount > account.BookBalance)
                    throw new ValidationException("Amount cannot exceed the bank account's book balance.");

                if (payment.Vendor_Id is not null &&
                    await connection.ExecuteScalarAsync<int>(
                        "SELECT COUNT(*) FROM vendor WHERE id = @Id", new { Id = payment.Vendor_Id }, tx) == 0)
                    throw new ValidationException("The vendor does not exist.");

                // The payment number is built from the row id, so the id is reserved first.
                var paymentId = await connection.ExecuteScalarAsync<long>(
                    "SELECT nextval(pg_get_serial_sequence('bank_payment', 'id'))", transaction: tx);
                var now = DateTime.UtcNow;
                var paymentNumber = $"PAY-{now.Year}-{paymentId:D6}";

                // 2. The Out transaction, party = payee.
                var transactionId = await connection.ExecuteScalarAsync<long>(
                    @"WITH next_id AS (SELECT nextval(pg_get_serial_sequence('""transaction""', 'id')) AS id)
                      INSERT INTO ""transaction"" (id, bank_account_id, number, type, source_number, direction, date, party, amount, description, status, created_at, created_by)
                      SELECT n.id, @BankAccountId, @NumberPrefix || LPAD(n.id::text, 6, '0'), @Type, @SourceNumber, @Direction::direction_enum, @Date, @Party, @Amount, @Description, @Status, @CreatedAt, @CreatedBy
                      FROM next_id n
                      RETURNING id",
                    new
                    {
                        BankAccountId = payment.Bank_Account_Id, NumberPrefix = $"TXN-{now.Year}-",
                        Type = nameof(TransactionType.BankPayment), SourceNumber = paymentNumber,
                        Direction = Direction.Out.ToDbValue(), Date = date, Party = payee, Amount = payment.Amount,
                        Description = description ?? $"Payment to {payee} ({category})",
                        Status = TransactionStatus.Posted.ToDbValue(), CreatedAt = now, CreatedBy
                    }, tx);

                // 3. The simulated statement line: a withdrawal, running balance continues the account's last one.
                var previous = await connection.QueryFirstOrDefaultAsync<decimal?>(
                    @"SELECT running_balance FROM bank_statement_line
                      WHERE bank_account_id = @BankAccountId ORDER BY posted_date DESC, id DESC LIMIT 1",
                    new { BankAccountId = payment.Bank_Account_Id }, tx);
                await connection.ExecuteAsync(
                    @"INSERT INTO bank_statement_line (bank_account_id, transaction_id, posted_date, description, party, type, direction, deposit, withdrawal, running_balance, statement_status, match_status, source, created_at)
                      VALUES (@BankAccountId, @TransactionId, @PostedDate, @Description, @Party, @Type, @Direction::direction_enum, 0, @Withdrawal, @RunningBalance, @StatementStatus, @MatchStatus, @Source, @CreatedAt)",
                    new
                    {
                        BankAccountId = payment.Bank_Account_Id, TransactionId = transactionId, PostedDate = date,
                        Description = description ?? $"Payment to {payee} ({category})", Party = payee,
                        Type = nameof(TransactionType.BankPayment), Direction = Direction.Out.ToDbValue(),
                        Withdrawal = payment.Amount,
                        RunningBalance = (previous ?? account.OpeningBalance) - payment.Amount,
                        StatementStatus = nameof(StatementStatus.Cleared), MatchStatus = nameof(MatchStatus.Matched),
                        Source = nameof(StatementSource.Simulated), CreatedAt = now
                    }, tx);

                // 4. The bank payment: not linked to an AP bill or a vehicle.
                await connection.ExecuteAsync(
                    @"INSERT INTO bank_payment (id, transaction_id, bank_account_id, vendor_id, ap_bill_id, vehicle_id, payment_number,
                                                payment_date, payment_method, reference, amount, payee, payment_category, description, status, created_by)
                      VALUES (@Id, @TransactionId, @BankAccountId, @VendorId, NULL, NULL, @PaymentNumber,
                              @PaymentDate, @PaymentMethod, @Reference, @Amount, @Payee, @PaymentCategory, @Description, @Status, @CreatedBy)",
                    new
                    {
                        Id = paymentId, TransactionId = transactionId, BankAccountId = payment.Bank_Account_Id,
                        VendorId = payment.Vendor_Id, PaymentNumber = paymentNumber, PaymentDate = date,
                        PaymentMethod = method, Reference = reference, Amount = payment.Amount, Payee = payee,
                        PaymentCategory = category, Description = description, Status = PostedStatus, CreatedBy
                    }, tx);

                // 5. Book balance goes down by the amount.
                var newBalance = account.BookBalance - payment.Amount;
                await connection.ExecuteAsync(
                    "UPDATE bank_account SET book_balance = @BookBalance WHERE id = @Id",
                    new { Id = payment.Bank_Account_Id, BookBalance = newBalance }, tx);

                tx.Commit();

                return new BankPaymentResponseDto
                {
                    Id = paymentId, Payment_Number = paymentNumber, Payment_Date = date, Payee = payee,
                    Payment_Category = category, Vendor_Id = payment.Vendor_Id, Payment_Method = method,
                    Reference = reference, Description = description, Amount = payment.Amount,
                    Bank_Account_Id = payment.Bank_Account_Id, Transaction_Id = transactionId,
                    Book_Balance = newBalance, Status = PostedStatus
                };
            }
            catch
            {
                if (tx.Connection is not null)
                    tx.Rollback();
                throw;
            }
        }

        // Cash | Check | Card | Transfer (any casing); returns the stored spelling.
        private static string ParsePaymentMethod(string? paymentMethod)
        {
            return Enum.GetNames<PaymentMethod>().FirstOrDefault(n => n.Equals(paymentMethod?.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new ValidationException("Payment method must be Cash, Check, Card or Transfer.");
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
