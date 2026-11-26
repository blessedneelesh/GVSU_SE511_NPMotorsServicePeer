using System.ComponentModel.DataAnnotations;
using System.Data;
using Dapper;
using Npgsql;
using NPMotors.Repository.Interface;
using NPMotors.Repository.Models;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.Models.Enums;
using NPMotors.Repository.RequestFeatures;

namespace NPMotors.Repository.Repositories
{
    public class TransactionRepository : ITransactionRepository
    {
        private readonly IRepositoryContext _context;

        public TransactionRepository(IRepositoryContext context)
        {
            _context = context;
        }

        private const string FromSql = "\"transaction\" t JOIN bank_account ba ON ba.id = t.bank_account_id";

        private const string TransactionColumns =
            "t.id, t.number, t.bank_account_id, ba.name AS bank_account_name, t.type, t.source_number, " +
            "t.direction::text AS direction, t.date::timestamp AS date, t.party, t.amount, t.description, " +
            "t.status, t.created_at, t.created_by, t.reverses_transaction_id, t.reversal_reason";

        // Reserves an id from the identity sequence so a transfer can build its reference from it.
        public async Task<long> GetNextTransactionId(IDbTransaction tx)
        {
            return await tx.Connection!.ExecuteScalarAsync<long>(
                "SELECT nextval(pg_get_serial_sequence('\"transaction\"', 'id'))", transaction: tx);
        }

        public async Task<long> InsertTransaction(Transaction transaction, IDbTransaction tx)
        {
            var queryParams = new DynamicParameters();
            queryParams.Add("Id", transaction.Id);
            queryParams.Add("BankAccountId", transaction.BankAccountId);
            queryParams.Add("NumberPrefix", $"TXN-{transaction.CreatedAt.Year}-");
            queryParams.Add("Type", transaction.Type.ToString());
            queryParams.Add("SourceNumber", transaction.SourceNumber);
            queryParams.Add("Direction", transaction.Direction.ToDbValue());
            queryParams.Add("Date", transaction.Date.Date, DbType.Date);
            queryParams.Add("Party", transaction.Party);
            queryParams.Add("Amount", transaction.Amount);
            queryParams.Add("Description", transaction.Description);
            queryParams.Add("Status", transaction.Status.ToDbValue());
            queryParams.Add("CreatedAt", transaction.CreatedAt);
            queryParams.Add("CreatedBy", transaction.CreatedBy);
            queryParams.Add("TransferReference", transaction.TransferReference);

            // The number is built from the row id (TXN-<year>-<id padded to 6>), so the id is
            // taken from the table's identity sequence first and inserted explicitly.
            // A caller that already reserved an id (Id > 0) gets that id.
            return await tx.Connection!.ExecuteScalarAsync<long>(
                @"WITH next_id AS (SELECT COALESCE(NULLIF(@Id, 0), nextval(pg_get_serial_sequence('""transaction""', 'id'))) AS id)
                  INSERT INTO ""transaction"" (id, bank_account_id, number, type, source_number, direction, date, party, amount, description, status, created_at, created_by, transfer_reference)
                  SELECT n.id, @BankAccountId, @NumberPrefix || LPAD(n.id::text, 6, '0'), @Type, @SourceNumber, @Direction::direction_enum, @Date, @Party, @Amount, @Description, @Status, @CreatedAt, @CreatedBy, @TransferReference
                  FROM next_id n
                  RETURNING id",
                queryParams, tx);
        }

        public async Task<PagedList<TransactionResponseDto>> GetAllTransactions(TransactionParameters parameters)
        {
            using IDbConnection connection = _context.GetDatabaseConnection();

            var whereClauses = new List<string>();
            var queryParams = new DynamicParameters();

            if (parameters.BankAccountId.HasValue)
            {
                whereClauses.Add("t.bank_account_id = @BankAccountId");
                queryParams.Add("BankAccountId", parameters.BankAccountId.Value);
            }

            var direction = parameters.Direction?.Trim().ToLowerInvariant();
            if (direction == "in" || direction == "out")
            {
                whereClauses.Add("t.direction = @Direction::direction_enum");
                queryParams.Add("Direction", direction);
            }

            if (parameters.DateFrom.HasValue)
            {
                whereClauses.Add("t.date >= @DateFrom");
                queryParams.Add("DateFrom", parameters.DateFrom.Value.Date, DbType.Date);
            }

            if (parameters.DateTo.HasValue)
            {
                whereClauses.Add("t.date <= @DateTo");
                queryParams.Add("DateTo", parameters.DateTo.Value.Date, DbType.Date);
            }

            var whereSql = whereClauses.Count > 0
                ? " WHERE " + string.Join(" AND ", whereClauses)
                : string.Empty;

            var count = await connection.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM {FromSql}{whereSql}", queryParams);

            queryParams.Add("Offset", (parameters.PageNumber - 1) * parameters.PageSize);
            queryParams.Add("Limit", parameters.PageSize);

            var transactions = (await connection.QueryAsync<TransactionResponseDto>(
                $"SELECT {TransactionColumns} FROM {FromSql}{whereSql} ORDER BY t.date DESC, t.id DESC OFFSET @Offset LIMIT @Limit",
                queryParams)).ToList();

            return new PagedList<TransactionResponseDto>(transactions, count, parameters.PageNumber, parameters.PageSize);
        }

        public async Task<TransactionResponseDto?> GetTransactionById(long id)
        {
            using IDbConnection connection = _context.GetDatabaseConnection();

            return await connection.QuerySingleOrDefaultAsync<TransactionResponseDto>(
                $"SELECT {TransactionColumns} FROM {FromSql} WHERE t.id = @Id",
                new { Id = id });
        }

        private const string ReversedStatus = "Reversed";

        private class AccountRow
        {
            public decimal OpeningBalance { get; set; }
            public decimal BookBalance { get; set; }
            public string Status { get; set; } = string.Empty;
        }

        // Read-only: the transaction with its receipt or payment, the documents that receipt or payment
        // paid down, and the reversal row (if any) that already reverses it. Null when not found.
        public async Task<ReversalSourceDto?> GetTransactionForReversal(long id)
        {
            using IDbConnection connection = _context.GetDatabaseConnection();

            var source = await connection.QuerySingleOrDefaultAsync<ReversalSourceDto>(
                @"SELECT t.id AS Transaction_Id, t.type, t.direction::text AS direction, t.amount, t.status,
                         t.bank_account_id, t.reverses_transaction_id, rev.id AS Reversed_By_Transaction_Id,
                         CASE WHEN br.id IS NOT NULL THEN 'Receipt' WHEN bp.id IS NOT NULL THEN 'Payment' ELSE '' END AS Source_Type,
                         COALESCE(br.id, bp.id, 0) AS Source_Id,
                         COALESCE(br.receipt_number, bp.payment_number, '') AS Source_Number,
                         COALESCE(br.status, bp.status, '') AS Source_Status
                  FROM ""transaction"" t
                  LEFT JOIN bank_receipt br ON br.transaction_id = t.id
                  LEFT JOIN bank_payment bp ON bp.transaction_id = t.id
                  LEFT JOIN ""transaction"" rev ON rev.reverses_transaction_id = t.id
                  WHERE t.id = @Id",
                new { Id = id });
            if (source is null)
                return null;

            source.Linked_Documents = (await connection.QueryAsync<SourceBalanceDto>(
                @"SELECT 'ARInvoice' AS Document_Type, ar.id AS Document_Id, ar.amount, ar.paid, ar.balance, ar.status
                  FROM bank_receipt br JOIN ar_invoice ar ON ar.id = br.ar_invoice_id
                  WHERE br.transaction_id = @Id
                  UNION ALL
                  SELECT 'SalesInvoice', si.id, COALESCE(si.total, 0), si.paid, si.balance, si.payment_status
                  FROM bank_receipt br JOIN sales_invoice si ON si.id = br.sales_invoice_id
                  WHERE br.transaction_id = @Id
                  UNION ALL
                  SELECT 'APBill', ab.id, ab.amount, ab.paid, ab.balance, ab.status
                  FROM bank_payment bp JOIN ap_bill ab ON ab.id = bp.ap_bill_id
                  WHERE bp.transaction_id = @Id",
                new { Id = id })).ToList();

            return source;
        }

        // One database transaction: any error rolls everything back, so all records are saved or none.
        public async Task<TransactionResponseDto> ReverseTransaction(ReversalPostingDto posting)
        {
            using IDbConnection connection = _context.GetDatabaseConnection();
            if (connection.State != ConnectionState.Open)
                connection.Open();
            using IDbTransaction tx = connection.BeginTransaction();

            try
            {
                // 1. Lock the bank account: it must be active and an outgoing reversal must be covered by the balance.
                var account = await connection.QuerySingleOrDefaultAsync<AccountRow>(
                    @"SELECT opening_balance AS OpeningBalance, book_balance AS BookBalance, LOWER(status) AS Status
                      FROM bank_account WHERE id = @Id FOR UPDATE",
                    new { Id = posting.Bank_Account_Id }, tx);
                if (account is null)
                    throw new ValidationException("The bank account does not exist.");
                if (account.Status != Status.Active.ToDbValue())
                    throw new ValidationException("The bank account must be active.");
                var isOut = posting.Direction == Direction.Out.ToDbValue();
                if (isOut && posting.Amount > account.BookBalance)
                    throw new ValidationException("The reversal cannot exceed the bank account's book balance.");

                var now = DateTime.UtcNow;
                var date = now.Date;

                // 2. The reversal transaction. The unique reverses_transaction_id blocks a second reversal
                //    made at the same time.
                long reversalId;
                try
                {
                    reversalId = await connection.ExecuteScalarAsync<long>(
                        @"WITH next_id AS (SELECT nextval(pg_get_serial_sequence('""transaction""', 'id')) AS id)
                          INSERT INTO ""transaction"" (id, bank_account_id, number, type, source_number, direction, date, party, amount, description,
                                                     status, reverses_transaction_id, reversal_reason, created_at, created_by)
                          SELECT n.id, @BankAccountId, @NumberPrefix || LPAD(n.id::text, 6, '0'), @Type, @SourceNumber, @Direction::direction_enum,
                                 @Date, o.party, @Amount, 'Reversal of ' || o.number, @Status, @OriginalId, @Reason, @CreatedAt, @CreatedBy
                          FROM next_id n JOIN ""transaction"" o ON o.id = @OriginalId
                          RETURNING id",
                        new
                        {
                            BankAccountId = posting.Bank_Account_Id, NumberPrefix = $"TXN-{now.Year}-",
                            Type = nameof(TransactionType.Reversal), SourceNumber = posting.Source_Number,
                            Direction = posting.Direction, Date = date, Amount = posting.Amount,
                            Status = TransactionStatus.Posted.ToDbValue(), OriginalId = posting.Original_Transaction_Id,
                            Reason = posting.Reason, CreatedAt = now, CreatedBy = "system"
                        }, tx);
                }
                catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
                {
                    throw new ValidationException("This transaction has already been reversed.");
                }

                // 3. The reversal's own statement line (simulated, matched); running balance continues the last one.
                var previous = await connection.QueryFirstOrDefaultAsync<decimal?>(
                    @"SELECT running_balance FROM bank_statement_line
                      WHERE bank_account_id = @BankAccountId ORDER BY posted_date DESC, id DESC LIMIT 1",
                    new { BankAccountId = posting.Bank_Account_Id }, tx);
                var signedAmount = isOut ? -posting.Amount : posting.Amount;
                var statementLineId = await connection.ExecuteScalarAsync<long>(
                    @"INSERT INTO bank_statement_line (bank_account_id, transaction_id, posted_date, description, party, type, direction, deposit, withdrawal,
                                                       running_balance, statement_status, match_status, source, created_at)
                      SELECT t.bank_account_id, t.id, t.date, t.description, t.party, t.type, t.direction, @Deposit, @Withdrawal,
                             @RunningBalance, @StatementStatus, @MatchStatus, @Source, @CreatedAt
                      FROM ""transaction"" t WHERE t.id = @TransactionId
                      RETURNING id",
                    new
                    {
                        TransactionId = reversalId, Deposit = isOut ? 0m : posting.Amount, Withdrawal = isOut ? posting.Amount : 0m,
                        RunningBalance = (previous ?? account.OpeningBalance) + signedAmount,
                        StatementStatus = nameof(StatementStatus.Cleared), MatchStatus = nameof(MatchStatus.Matched),
                        Source = nameof(StatementSource.Simulated), CreatedAt = now
                    }, tx);

                // 4. Book balance: minus the amount for "out", plus it for "in".
                var newBalance = account.BookBalance + signedAmount;
                await connection.ExecuteAsync(
                    "UPDATE bank_account SET book_balance = @BookBalance WHERE id = @Id",
                    new { Id = posting.Bank_Account_Id, BookBalance = newBalance }, tx);

                // 5. The original receipt or payment is kept and marked reversed.
                var sourceTable = posting.Source_Type == "Receipt" ? "bank_receipt" : "bank_payment";
                var sourceRows = await connection.ExecuteAsync(
                    $"UPDATE {sourceTable} SET status = @Status WHERE id = @Id",
                    new { Id = posting.Source_Id, Status = ReversedStatus }, tx);
                if (sourceRows == 0)
                    throw new ValidationException("The original receipt or payment no longer exists.");

                // 6. Restore each document. The WHERE on the old paid amount means 0 rows if another change got there first.
                foreach (var document in posting.Restored_Documents)
                {
                    var documentRows = await connection.ExecuteAsync(
                        $"UPDATE {DocumentTable(document.Document_Type)} SET paid = @Paid, balance = @Balance, {DocumentStatusColumn(document.Document_Type)} = @Status " +
                        "WHERE id = @Id AND paid = @ExpectedPaid",
                        new
                        {
                            Id = document.Document_Id, document.Paid, document.Balance,
                            Status = document.Status.ToLowerInvariant(), ExpectedPaid = document.Paid + posting.Amount
                        }, tx);
                    if (documentRows == 0)
                        throw new ValidationException("A linked document was changed by another payment. Reload it and try again.");
                }

                // 7. Customer.Balance / Vendor.Balance = sum of the open AR / AP balances.
                if (posting.Restored_Documents.Count > 0)
                    await RecalculatePartyBalance(connection, posting, tx);

                var reversal = await connection.QuerySingleAsync<TransactionResponseDto>(
                    $"SELECT {TransactionColumns} FROM {FromSql} WHERE t.id = @Id",
                    new { Id = reversalId }, tx);
                reversal.Statement_Line_Id = statementLineId;
                reversal.Book_Balance = newBalance;

                tx.Commit();
                return reversal;
            }
            catch
            {
                if (tx.Connection is not null)
                    tx.Rollback();
                throw;
            }
        }

        private static string DocumentTable(string documentType) => documentType switch
        {
            "ARInvoice" => "ar_invoice",
            "SalesInvoice" => "sales_invoice",
            "APBill" => "ap_bill",
            _ => throw new InvalidOperationException($"Unknown document type '{documentType}'.")
        };

        private static string DocumentStatusColumn(string documentType) =>
            documentType == "SalesInvoice" ? "payment_status" : "status";

        private static async Task RecalculatePartyBalance(IDbConnection connection, ReversalPostingDto posting, IDbTransaction tx)
        {
            if (posting.Source_Type == "Receipt")
            {
                var customerId = await connection.ExecuteScalarAsync<long?>(
                    @"SELECT COALESCE(br.customer_id, si.customer_id, si2.customer_id)
                      FROM bank_receipt br
                      LEFT JOIN sales_invoice si ON si.id = br.sales_invoice_id
                      LEFT JOIN ar_invoice ar ON ar.id = br.ar_invoice_id
                      LEFT JOIN sales_invoice si2 ON si2.id = ar.invoice_id
                      WHERE br.id = @Id",
                    new { Id = posting.Source_Id }, tx);
                if (customerId is null)
                    return;

                await connection.ExecuteAsync(
                    @"UPDATE customer
                      SET balance = GREATEST(0, (SELECT COALESCE(SUM(ar.balance), 0)
                                                 FROM ar_invoice ar JOIN sales_invoice si ON si.id = ar.invoice_id
                                                 WHERE si.customer_id = @CustomerId AND ar.balance > 0))
                      WHERE id = @CustomerId",
                    new { CustomerId = customerId }, tx);
            }
            else
            {
                var vendorId = await connection.ExecuteScalarAsync<long?>(
                    @"SELECT COALESCE(bp.vendor_id, ab.vendor_id)
                      FROM bank_payment bp LEFT JOIN ap_bill ab ON ab.id = bp.ap_bill_id
                      WHERE bp.id = @Id",
                    new { Id = posting.Source_Id }, tx);
                if (vendorId is null)
                    return;

                await connection.ExecuteAsync(
                    @"UPDATE vendor
                      SET balance = (SELECT COALESCE(SUM(balance), 0) FROM ap_bill WHERE vendor_id = @VendorId AND balance > 0)
                      WHERE id = @VendorId",
                    new { VendorId = vendorId }, tx);
            }
        }
    }
}
