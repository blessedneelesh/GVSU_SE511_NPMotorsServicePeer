using System.Data;
using Dapper;

namespace Module.Tests
{
    // Story 23 test data: a bank, an account and the paid documents a receipt or payment can be reversed from.
    // Plain SQL, so the seed does not depend on the code under test. Everything carries the marker and is removed by Cleanup.
    public class ReversalSeed
    {
        private readonly Func<IDbConnection> _open;
        private int _sequence;

        public string Marker { get; } = $"itest-{Guid.NewGuid():N}".Substring(0, 18);
        public long BankId { get; private set; }
        public long AccountId { get; private set; }
        public long CustomerId { get; private set; }
        public long SalespersonId { get; private set; }
        public long VendorId { get; private set; }

        public record Seeded(long TransactionId, long SourceId, long? ArId = null, long? InvoiceId = null, long? BillId = null);

        public ReversalSeed(Func<IDbConnection> open)
        {
            _open = open;
        }

        public IDbConnection Open() => _open();

        public async Task InitializeAsync(decimal balance = 50000m, string accountStatus = "active")
        {
            using var c = _open();
            BankId = await c.ExecuteScalarAsync<long>(
                "INSERT INTO bank (name) VALUES (@Name) RETURNING id", new { Name = $"{Marker} Bank" });
            AccountId = await c.ExecuteScalarAsync<long>(
                @"INSERT INTO bank_account (bank_id, name, type, opening_balance, book_balance, status)
                  VALUES (@BankId, @Name, 'Checking', 0, @Balance, @Status) RETURNING id",
                new { BankId, Name = $"{Marker} Account", Balance = balance, Status = accountStatus });
            CustomerId = await c.ExecuteScalarAsync<long>(
                "INSERT INTO customer (first_name, last_name) VALUES (@Name, 'Buyer') RETURNING id", new { Name = Marker });
            SalespersonId = await c.ExecuteScalarAsync<long>(
                "INSERT INTO employee (first_name, last_name) VALUES (@Name, 'Seller') RETURNING id", new { Name = Marker });
            VendorId = await c.ExecuteScalarAsync<long>(
                "INSERT INTO vendor (name) VALUES (@Name) RETURNING id", new { Name = $"{Marker} Vendor" });
        }

        public async Task CleanupAsync()
        {
            using var c = _open();
            var p = new { Pattern = Marker + "%", BankId };
            const string accounts = "SELECT id FROM bank_account WHERE bank_id = @BankId";
            await c.ExecuteAsync($"DELETE FROM bank_statement_line WHERE bank_account_id IN ({accounts})", p);
            await c.ExecuteAsync($"DELETE FROM bank_receipt WHERE bank_account_id IN ({accounts})", p);
            await c.ExecuteAsync($"DELETE FROM bank_payment WHERE bank_account_id IN ({accounts})", p);
            await c.ExecuteAsync($@"DELETE FROM ""transaction"" WHERE reverses_transaction_id IS NOT NULL AND bank_account_id IN ({accounts})", p);
            await c.ExecuteAsync($@"DELETE FROM ""transaction"" WHERE bank_account_id IN ({accounts})", p);
            await c.ExecuteAsync(
                "DELETE FROM ar_invoice WHERE invoice_id IN (SELECT id FROM sales_invoice WHERE customer_id IN (SELECT id FROM customer WHERE first_name LIKE @Pattern))", p);
            await c.ExecuteAsync("DELETE FROM sales_invoice WHERE customer_id IN (SELECT id FROM customer WHERE first_name LIKE @Pattern)", p);
            await c.ExecuteAsync("DELETE FROM ap_bill WHERE vendor_id IN (SELECT id FROM vendor WHERE name LIKE @Pattern)", p);
            await c.ExecuteAsync("DELETE FROM customer WHERE first_name LIKE @Pattern", p);
            await c.ExecuteAsync("DELETE FROM employee WHERE first_name LIKE @Pattern", p);
            await c.ExecuteAsync("DELETE FROM vendor WHERE name LIKE @Pattern", p);
            await c.ExecuteAsync("DELETE FROM bank_account WHERE bank_id = @BankId", p);
            await c.ExecuteAsync("DELETE FROM bank WHERE id = @BankId", p);
        }

        private int Next() => ++_sequence;

        // A posted receipt that paid down an AR invoice and its sales invoice (both end up with paid = paidAfter).
        public async Task<Seeded> SeedArReceiptAsync(decimal receiptAmount = 5000m, decimal invoiceTotal = 20000m, decimal paidAfter = 15000m)
        {
            using var c = _open();
            var n = Next();
            var balance = invoiceTotal - paidAfter;
            var invoiceId = await c.ExecuteScalarAsync<long>(
                @"INSERT INTO sales_invoice (number, customer_id, salesperson_id, total, paid, balance, payment_status, created_by)
                  VALUES (@Number, @CustomerId, @SalespersonId, @Total, @Paid, @Balance, 'partial', 'itest') RETURNING id",
                new { Number = $"{Marker}-SI-{n}", CustomerId, SalespersonId, Total = invoiceTotal, Paid = paidAfter, Balance = balance });
            var arId = await c.ExecuteScalarAsync<long>(
                @"INSERT INTO ar_invoice (invoice_id, number, amount, paid, balance, status, created_by)
                  VALUES (@InvoiceId, @Number, @Total, @Paid, @Balance, 'partial', 'itest') RETURNING id",
                new { InvoiceId = invoiceId, Number = $"AR-SEED-{n}", Total = invoiceTotal, Paid = paidAfter, Balance = balance });
            await c.ExecuteAsync("UPDATE customer SET balance = @Balance WHERE id = @Id", new { Balance = balance, Id = CustomerId });

            var transactionId = await InsertTransactionAsync(c, "BankReceipt", "in", receiptAmount, $"SI-{n}");
            var receiptId = await c.ExecuteScalarAsync<long>(
                @"INSERT INTO bank_receipt (transaction_id, bank_account_id, customer_id, ar_invoice_id, sales_invoice_id, receipt_number,
                                            receipt_date, payment_method, amount, status, created_by)
                  VALUES (@TransactionId, @AccountId, @CustomerId, @ArId, @InvoiceId, @Number, @Today, 'Card', @Amount, 'Posted', 'itest') RETURNING id",
                new { TransactionId = transactionId, AccountId, CustomerId, ArId = arId, InvoiceId = invoiceId,
                      Number = $"RCT-SEED-{n}", Today = DateTime.UtcNow.Date, Amount = receiptAmount });
            await InsertStatementLineAsync(c, transactionId, "BankReceipt", "in", receiptAmount);
            return new Seeded(transactionId, receiptId, arId, invoiceId);
        }

        // A posted payment on an AP bill (paidAfter is what the bill shows after that payment).
        public async Task<Seeded> SeedApPaymentAsync(decimal paymentAmount = 3000m, decimal billAmount = 8000m, decimal paidAfter = 3000m)
        {
            using var c = _open();
            var n = Next();
            var balance = billAmount - paidAfter;
            var billId = await c.ExecuteScalarAsync<long>(
                @"INSERT INTO ap_bill (number, vendor_id, amount, paid, balance, status, created_by)
                  VALUES (@Number, @VendorId, @Amount, @Paid, @Balance, 'partial', 'itest') RETURNING id",
                new { Number = $"AP-SEED-{n}", VendorId, Amount = billAmount, Paid = paidAfter, Balance = balance });
            await c.ExecuteAsync("UPDATE vendor SET balance = @Balance WHERE id = @Id", new { Balance = balance, Id = VendorId });

            var transactionId = await InsertTransactionAsync(c, "BankPayment", "out", paymentAmount, $"AP-SEED-{n}");
            var paymentId = await c.ExecuteScalarAsync<long>(
                @"INSERT INTO bank_payment (transaction_id, bank_account_id, vendor_id, ap_bill_id, payment_number, payment_date,
                                            payment_method, amount, status, created_by)
                  VALUES (@TransactionId, @AccountId, @VendorId, @BillId, @Number, @Today, 'Check', @Amount, 'Posted', 'itest') RETURNING id",
                new { TransactionId = transactionId, AccountId, VendorId, BillId = billId,
                      Number = $"PAY-SEED-{n}", Today = DateTime.UtcNow.Date, Amount = paymentAmount });
            await InsertStatementLineAsync(c, transactionId, "BankPayment", "out", paymentAmount);
            return new Seeded(transactionId, paymentId, BillId: billId);
        }

        // A receipt that is not linked to any invoice, so it has no documents to restore.
        public async Task<Seeded> SeedMiscReceiptAsync(decimal amount = 1000m)
        {
            using var c = _open();
            var n = Next();
            var transactionId = await InsertTransactionAsync(c, "BankReceipt", "in", amount, "BankReceipt");
            var receiptId = await c.ExecuteScalarAsync<long>(
                @"INSERT INTO bank_receipt (transaction_id, bank_account_id, receipt_number, receipt_date, payment_method, amount, payer, status, created_by)
                  VALUES (@TransactionId, @AccountId, @Number, @Today, 'Cash', @Amount, 'Walk-in', 'Posted', 'itest') RETURNING id",
                new { TransactionId = transactionId, AccountId, Number = $"RCT-SEED-{n}", Today = DateTime.UtcNow.Date, Amount = amount });
            await InsertStatementLineAsync(c, transactionId, "BankReceipt", "in", amount);
            return new Seeded(transactionId, receiptId);
        }

        // A posted transaction that is neither a receipt nor a payment.
        public async Task<Seeded> SeedPlainTransactionAsync(string type = "BankAdjustment", string direction = "in", decimal amount = 100m)
        {
            using var c = _open();
            var transactionId = await InsertTransactionAsync(c, type, direction, amount, "ADJ-1");
            return new Seeded(transactionId, 0);
        }

        public async Task SetBookBalanceAsync(decimal balance)
        {
            using var c = _open();
            await c.ExecuteAsync("UPDATE bank_account SET book_balance = @Balance WHERE id = @Id", new { Balance = balance, Id = AccountId });
        }

        public async Task<decimal> GetBookBalanceAsync()
        {
            using var c = _open();
            return await c.ExecuteScalarAsync<decimal>("SELECT book_balance FROM bank_account WHERE id = @Id", new { Id = AccountId });
        }

        public async Task<int> CountTransactionsAsync()
        {
            using var c = _open();
            return await c.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM ""transaction"" WHERE bank_account_id = @Id", new { Id = AccountId });
        }

        public async Task<int> CountStatementLinesAsync()
        {
            using var c = _open();
            return await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM bank_statement_line WHERE bank_account_id = @Id", new { Id = AccountId });
        }

        private async Task<long> InsertTransactionAsync(IDbConnection c, string type, string direction, decimal amount, string sourceNumber)
        {
            return await c.ExecuteScalarAsync<long>(
                @"INSERT INTO ""transaction"" (bank_account_id, type, direction, number, date, party, amount, status, description, source_number, created_by)
                  VALUES (@AccountId, @Type, @Direction::direction_enum, @Number, @Today, 'Acme', @Amount, 'posted', 'seed', @SourceNumber, 'itest')
                  RETURNING id",
                new { AccountId, Type = type, Direction = direction, Number = $"TXN-SEED-{Next():D6}", Today = DateTime.UtcNow.Date, Amount = amount, SourceNumber = sourceNumber });
        }

        // The original posting's statement line; its running balance is the account's current book balance.
        private async Task InsertStatementLineAsync(IDbConnection c, long transactionId, string type, string direction, decimal amount)
        {
            var running = await c.ExecuteScalarAsync<decimal>("SELECT book_balance FROM bank_account WHERE id = @Id", new { Id = AccountId });
            await c.ExecuteAsync(
                @"INSERT INTO bank_statement_line (bank_account_id, transaction_id, posted_date, description, party, type, direction, deposit, withdrawal,
                                                   running_balance, statement_status, match_status, source)
                  VALUES (@AccountId, @TransactionId, @Today, 'seed', 'Acme', @Type, @Direction::direction_enum, @Deposit, @Withdrawal,
                          @Running, 'Cleared', 'Matched', 'Simulated')",
                new { AccountId, TransactionId = transactionId, Today = DateTime.UtcNow.Date, Type = type, Direction = direction,
                      Deposit = direction == "in" ? amount : 0m, Withdrawal = direction == "out" ? amount : 0m, Running = running });
        }
    }
}
