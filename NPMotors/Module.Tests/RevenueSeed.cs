using System.Data;
using Dapper;

namespace Module.Tests
{
    // Story 26 test data: sales invoices and bank adjustments in plain SQL, so the seed does not depend on the code under test.
    // Tests use dates in the year 2090 so no other data falls inside the period. Everything carries the marker and is removed by Cleanup.
    public class RevenueSeed
    {
        private readonly Func<IDbConnection> _open;
        private int _sequence;

        public string Marker { get; } = $"itest-{Guid.NewGuid():N}".Substring(0, 18);
        public long BankId { get; private set; }
        public long AccountId { get; private set; }
        public long CustomerId { get; private set; }
        public long SalespersonId { get; private set; }
        public long VendorId { get; private set; }

        public RevenueSeed(Func<IDbConnection> open)
        {
            _open = open;
        }

        public async Task InitializeAsync()
        {
            using var c = _open();
            BankId = await c.ExecuteScalarAsync<long>(
                "INSERT INTO bank (name) VALUES (@Name) RETURNING id", new { Name = $"{Marker} Bank" });
            AccountId = await c.ExecuteScalarAsync<long>(
                @"INSERT INTO bank_account (bank_id, name, type, opening_balance, book_balance, status)
                  VALUES (@BankId, @Name, 'Checking', 0, 0, 'active') RETURNING id",
                new { BankId, Name = $"{Marker} Account" });
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
            await c.ExecuteAsync($@"DELETE FROM ""transaction"" WHERE reverses_transaction_id IS NOT NULL AND bank_account_id IN ({accounts})", p);
            await c.ExecuteAsync($@"DELETE FROM ""transaction"" WHERE bank_account_id IN ({accounts})", p);
            await c.ExecuteAsync("DELETE FROM sales_invoice WHERE customer_id IN (SELECT id FROM customer WHERE first_name LIKE @Pattern)", p);
            await c.ExecuteAsync("DELETE FROM ap_bill WHERE vendor_id = @VendorId", new { VendorId });
            await c.ExecuteAsync("DELETE FROM vehicle WHERE vendor_id = @VendorId", new { VendorId });
            await c.ExecuteAsync("DELETE FROM vendor WHERE id = @VendorId", new { VendorId });
            await c.ExecuteAsync("DELETE FROM customer WHERE first_name LIKE @Pattern", p);
            await c.ExecuteAsync("DELETE FROM employee WHERE first_name LIKE @Pattern", p);
            await c.ExecuteAsync("DELETE FROM bank_account WHERE bank_id = @BankId", p);
            await c.ExecuteAsync("DELETE FROM bank WHERE id = @BankId", p);
        }

        private int Next() => ++_sequence;

        public async Task<long> AddInvoiceAsync(DateTime date, decimal salePrice, decimal fees = 0m, decimal discount = 0m, decimal tax = 0m,
            string lifecycle = "posted", string paymentStatus = "unpaid", long? vehicleId = null)
        {
            using var c = _open();
            var total = salePrice + tax + fees - discount;
            var paid = paymentStatus == "paid" ? total : 0m;
            return await c.ExecuteScalarAsync<long>(
                @"INSERT INTO sales_invoice (number, date, customer_id, salesperson_id, vehicle_id, sale_price, tax, fees, discount, total, paid, balance,
                                             payment_status, lifecycle, cancel_reason, created_by)
                  VALUES (@Number, @Date, @CustomerId, @SalespersonId, @VehicleId, @SalePrice, @Tax, @Fees, @Discount, @Total, @Paid, @Balance,
                          @PaymentStatus, @Lifecycle, @CancelReason, 'itest')
                  RETURNING id",
                new
                {
                    Number = $"{Marker}-SI-{Next()}", Date = date.Date, CustomerId, SalespersonId, VehicleId = vehicleId, SalePrice = salePrice, Tax = tax,
                    Fees = fees, Discount = discount, Total = total, Paid = paid, Balance = total - paid, PaymentStatus = paymentStatus,
                    Lifecycle = lifecycle, CancelReason = lifecycle == "cancelled" ? "itest cancel" : null
                });
        }

        // A vehicle of the seeded vendor with a stored total_cost (purchase_price + additional_costs).
        public async Task<long> AddVehicleAsync(decimal purchasePrice, decimal additionalCosts = 0m, string status = "available")
        {
            using var c = _open();
            return await c.ExecuteScalarAsync<long>(
                @"INSERT INTO vehicle (vin, vendor_id, make, model, purchase_price, additional_costs, total_cost, selling_price, status, created_by)
                  VALUES (@Vin, @VendorId, 'Honda', 'Civic', @Purchase, @Additional, @Total, 0, @Status, 'itest')
                  RETURNING id",
                new
                {
                    Vin = Guid.NewGuid().ToString("N").Substring(0, 17).ToUpperInvariant(), VendorId, Purchase = purchasePrice,
                    Additional = additionalCosts, Total = purchasePrice + additionalCosts, Status = status
                });
        }

        // An AP bill for the seeded vendor (the vendor side of a vehicle purchase).
        public async Task AddApBillAsync(decimal amount, decimal paid)
        {
            using var c = _open();
            await c.ExecuteAsync(
                @"INSERT INTO ap_bill (number, vendor_id, amount, paid, balance, status, created_by)
                  VALUES (@Number, @VendorId, @Amount, @Paid, @Balance, @Status, 'itest')",
                new
                {
                    Number = $"{Marker}-AP-{Next()}", VendorId, Amount = amount, Paid = paid, Balance = amount - paid,
                    Status = paid >= amount ? "paid" : paid > 0 ? "partial" : "unpaid"
                });
        }

        // A bank adjustment: a transaction plus its matched statement line. Returns the transaction id.
        public async Task<long> AddAdjustmentAsync(DateTime date, decimal amount, string? adjustmentType, string direction = "in",
            string status = "posted", string type = "BankAdjustment")
        {
            using var c = _open();
            var transactionId = await InsertTransactionAsync(c, date, amount, direction, status, type, null);
            await InsertStatementLineAsync(c, transactionId, date, amount, direction, type, adjustmentType, "Matched", "Simulated");
            return transactionId;
        }

        // A statement-only line (Story 24): no transaction, Manual source, Unmatched.
        public async Task AddStatementOnlyAdjustmentAsync(DateTime date, decimal amount, string adjustmentType)
        {
            using var c = _open();
            await InsertStatementLineAsync(c, null, date, amount, "in", "BankAdjustment", adjustmentType, "Unmatched", "Manual");
        }

        // A transaction that reverses another one (outgoing, posted).
        public async Task<long> AddReversalAsync(long reversesTransactionId, DateTime date, decimal amount)
        {
            using var c = _open();
            return await InsertTransactionAsync(c, date, amount, "out", "posted", "Reversal", reversesTransactionId);
        }

        // An AR receipt or a transfer: a posted incoming transaction with a matched line but no adjustment type.
        public Task<long> AddNonAdjustmentIncomingAsync(DateTime date, decimal amount, string type) =>
            AddAdjustmentAsync(date, amount, null, "in", "posted", type);

        private async Task<long> InsertTransactionAsync(IDbConnection c, DateTime date, decimal amount, string direction, string status,
            string type, long? reversesTransactionId)
        {
            return await c.ExecuteScalarAsync<long>(
                @"INSERT INTO ""transaction"" (bank_account_id, type, direction, number, date, party, amount, status, description, source_number,
                                               reverses_transaction_id, reversal_reason, created_by)
                  VALUES (@AccountId, @Type, @Direction::direction_enum, @Number, @Date, 'Acme', @Amount, @Status, 'seed', @SourceNumber,
                          @ReversesId, @Reason, 'itest')
                  RETURNING id",
                new
                {
                    AccountId, Type = type, Direction = direction, Number = $"{Marker}-T{Next():D4}", Date = date.Date, Amount = amount,
                    Status = status, SourceNumber = $"{Marker}-S", ReversesId = reversesTransactionId,
                    Reason = reversesTransactionId is null ? null : "itest reversal"
                });
        }

        private async Task InsertStatementLineAsync(IDbConnection c, long? transactionId, DateTime date, decimal amount, string direction,
            string type, string? adjustmentType, string matchStatus, string source)
        {
            await c.ExecuteAsync(
                @"INSERT INTO bank_statement_line (bank_account_id, transaction_id, posted_date, description, party, type, adjustment_type,
                                                   direction, deposit, withdrawal, running_balance, statement_status, match_status, source)
                  VALUES (@AccountId, @TransactionId, @Date, 'seed', 'Acme', @Type, @AdjustmentType, @Direction::direction_enum,
                          @Deposit, @Withdrawal, 0, 'Cleared', @MatchStatus, @Source)",
                new
                {
                    AccountId, TransactionId = transactionId, Date = date.Date, Type = type, AdjustmentType = adjustmentType,
                    Direction = direction, Deposit = direction == "in" ? amount : 0m, Withdrawal = direction == "out" ? amount : 0m,
                    MatchStatus = matchStatus, Source = source
                });
        }
    }
}
