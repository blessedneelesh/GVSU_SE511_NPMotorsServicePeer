using System.Data;
using Dapper;
using NPMotors.Repository.Models;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.Models.Enums;
using NPMotors.Repository.Repositories;

namespace NPMotors.Repository.Tests.DataAccess
{
    // Story 14: ArInvoiceRepository.InsertArInvoice and BankReceiptRepository.InsertBankReceipt.
    // Every insert runs inside a transaction that is never committed, so nothing stays in the database;
    // only the draft invoice (needed for the foreign keys) is removed by the marker sweep.
    public class ArInvoiceAndBankReceiptRepositoryTest : TestBase, IAsyncLifetime
    {
        private readonly ArInvoiceRepository _arRepository = new();
        private readonly BankReceiptRepository _receiptRepository = new();
        private readonly string _marker = $"itest-{Guid.NewGuid():N}".Substring(0, 12);
        private long _customerId;
        private long _invoiceId;

        private class ArRow
        {
            public long Invoice_Id { get; set; }
            public string Number { get; set; } = "";
            public DateTime Date { get; set; }
            public DateTime? Due_Date { get; set; }
            public decimal Amount { get; set; }
            public decimal Paid { get; set; }
            public decimal Balance { get; set; }
            public string Status { get; set; } = "";
            public string Created_By { get; set; } = "";
        }

        private class ReceiptRow
        {
            public long Transaction_Id { get; set; }
            public long Bank_Account_Id { get; set; }
            public long? Customer_Id { get; set; }
            public long? Sales_Invoice_Id { get; set; }
            public long? Ar_Invoice_Id { get; set; }
            public string Receipt_Number { get; set; } = "";
            public DateTime Receipt_Date { get; set; }
            public string Payment_Method { get; set; } = "";
            public string? Reference { get; set; }
            public decimal Amount { get; set; }
            public string Status { get; set; } = "";
            public string Created_By { get; set; } = "";
        }

        public async Task InitializeAsync()
        {
            _customerId = (await new CustomerRepository(_repositoryContext).AddCustomer(new CustomerCreateDto
            {
                First_Name = _marker, Last_Name = "Buyer", Email = $"{_marker}@example.com", Status = Status.Active
            })).Id;
            var salespersonId = (await new EmployeeRepository(_repositoryContext).AddEmployee(new EmployeeCreateDto
            {
                First_Name = _marker, Last_Name = "Seller", Department = "Sales", Commission_Rate = 5m, Status = Status.Active
            })).Id;
            var vendor = await new VendorRepository(_repositoryContext).AddVendor(new VendorCreateDto
            {
                Name = $"{_marker} Vendor", Type = VendorType.Vehicles, Status = Status.Active
            });
            var vehicle = await new VehicleRepository(_repositoryContext).AddVehicle(new VehicleCreateDto
            {
                Vendor_Id = vendor.Id,
                Vin = $"{_marker}1".ToUpperInvariant().PadRight(17, '0').Substring(0, 17),
                Make = "Toyota", Model = "Camry",
                Purchase_Price = 20000m, Additional_Costs = 500m, Selling_Price = 25000m
            });
            _invoiceId = (await new SalesInvoiceRepository(_repositoryContext).AddSalesInvoice(new SalesInvoiceCreateDto
            {
                Customer_Id = _customerId, Salesperson_Id = salespersonId, Vehicle_Id = vehicle.Id,
                Invoice_Date = new DateTime(2026, 5, 1), Tax = 1000m, Fees = 200m, Discount = 700m
            })).Id;
        }

        public async Task DisposeAsync()
        {
            using IDbConnection connection = _repositoryContext.GetDatabaseConnection();
            var pattern = new { Pattern = _marker + "%", VinPattern = _marker.ToUpperInvariant() + "%" };
            const string markerVehicles =
                "SELECT id FROM vehicle WHERE vendor_id IN (SELECT id FROM vendor WHERE name LIKE @Pattern) OR vin LIKE @VinPattern";
            await connection.ExecuteAsync($"UPDATE vehicle SET invoice_id = NULL WHERE id IN ({markerVehicles})", pattern);
            await connection.ExecuteAsync(
                $@"DELETE FROM sales_invoice
                   WHERE vehicle_id IN ({markerVehicles})
                      OR customer_id IN (SELECT id FROM customer WHERE first_name LIKE @Pattern)", pattern);
            await connection.ExecuteAsync($"DELETE FROM vehicle WHERE id IN ({markerVehicles})", pattern);
            await connection.ExecuteAsync("DELETE FROM vendor WHERE name LIKE @Pattern", pattern);
            await connection.ExecuteAsync("DELETE FROM customer WHERE first_name LIKE @Pattern", pattern);
            await connection.ExecuteAsync("DELETE FROM employee WHERE first_name LIKE @Pattern", pattern);
        }

        private static (IDbConnection Connection, IDbTransaction Tx) Begin()
        {
            var connection = _repositoryContext.GetDatabaseConnection();
            if (connection.State != ConnectionState.Open)
                connection.Open();
            return (connection, connection.BeginTransaction());
        }

        private ArInvoice NewAr(PaymentStatus status = PaymentStatus.Partial, decimal paid = 10000m, DateTime? dueDate = null) => new()
        {
            InvoiceId = _invoiceId,
            Date = new DateTime(2026, 11, 8, 14, 30, 0),
            DueDate = dueDate ?? new DateTime(2026, 12, 8),
            Amount = 25500m,
            Paid = paid,
            Balance = 25500m - paid,
            Status = status,
            CreatedAt = new DateTime(2026, 11, 8, 14, 30, 0),
            CreatedBy = "system"
        };

        private BankReceipt NewReceipt(long transactionId, long accountId, long? arInvoiceId = null,
            PaymentMethod method = PaymentMethod.Card, string? reference = "WIRE-77") => new()
        {
            TransactionId = transactionId,
            BankAccountId = accountId,
            CustomerId = _customerId,
            SalesInvoiceId = _invoiceId,
            ArInvoiceId = arInvoiceId,
            ReceiptDate = new DateTime(2026, 11, 8, 9, 15, 0),
            PaymentMethod = method,
            Reference = reference,
            Amount = 10000m,
            Status = "Posted",
            CreatedAt = new DateTime(2026, 11, 8, 9, 15, 0),
            CreatedBy = "system"
        };

        // Inserts a bank, an account and a posted receipt transaction, all inside tx.
        private async Task<(long AccountId, long TransactionId)> InsertTransactionAsync(IDbTransaction tx)
        {
            var connection = tx.Connection!;
            var bankId = await connection.ExecuteScalarAsync<long>(
                "INSERT INTO bank (name) VALUES (@Name) RETURNING id", new { Name = $"{_marker} Bank" }, tx);
            var accountId = await connection.ExecuteScalarAsync<long>(
                @"INSERT INTO bank_account (bank_id, name, type, opening_balance, book_balance, status)
                  VALUES (@BankId, @Name, 'Checking', 0, 0, 'active') RETURNING id",
                new { BankId = bankId, Name = $"{_marker} Account" }, tx);
            var transactionId = await InsertTransactionRowAsync(tx, accountId);
            return (accountId, transactionId);
        }

        private async Task<long> InsertTransactionRowAsync(IDbTransaction tx, long accountId) =>
            await tx.Connection!.ExecuteScalarAsync<long>(
                @"INSERT INTO ""transaction"" (bank_account_id, type, direction, number, date, amount, status, source_number, created_by)
                  VALUES (@AccountId, 'BankReceipt', 'in'::direction_enum, @Number, CURRENT_DATE, 10000, 'posted', 'INV-TEST', 'system')
                  RETURNING id",
                new { AccountId = accountId, Number = $"{_marker}-{Guid.NewGuid():N}".Substring(0, 30) }, tx);

        // ---- InsertArInvoice ----

        [Fact]
        public async Task InsertArInvoice_StoresEveryColumn()
        {
            var (connection, tx) = Begin();
            using (connection) using (tx)
            {
                var id = await _arRepository.InsertArInvoice(NewAr(PaymentStatus.Partial, 10000m), tx);

                Assert.True(id > 0);
                var row = await connection.QuerySingleAsync<ArRow>(
                    "SELECT invoice_id, number, date, due_date, amount, paid, balance, status, created_by FROM ar_invoice WHERE id = @Id",
                    new { Id = id }, tx);
                Assert.Equal(_invoiceId, row.Invoice_Id);
                Assert.Equal(new DateTime(2026, 11, 8), row.Date);
                Assert.Equal(new DateTime(2026, 12, 8), row.Due_Date);
                Assert.Equal(25500m, row.Amount);
                Assert.Equal(10000m, row.Paid);
                Assert.Equal(15500m, row.Balance);
                Assert.Equal("partial", row.Status);
                Assert.Equal("system", row.Created_By);
            }
        }

        [Fact]
        public async Task InsertArInvoice_NumberIsArPrefixPlusIdPaddedToSixDigits()
        {
            var (connection, tx) = Begin();
            using (connection) using (tx)
            {
                var id = await _arRepository.InsertArInvoice(NewAr(), tx);

                var number = await connection.ExecuteScalarAsync<string>(
                    "SELECT number FROM ar_invoice WHERE id = @Id", new { Id = id }, tx);
                Assert.Equal($"AR-{id:D6}", number);
            }
        }

        [Fact]
        public async Task InsertArInvoice_CreditSale_StoresUnpaidWithFullBalance()
        {
            var (connection, tx) = Begin();
            using (connection) using (tx)
            {
                var id = await _arRepository.InsertArInvoice(NewAr(PaymentStatus.Unpaid, 0m), tx);

                var row = await connection.QuerySingleAsync<ArRow>(
                    "SELECT invoice_id, number, date, due_date, amount, paid, balance, status, created_by FROM ar_invoice WHERE id = @Id",
                    new { Id = id }, tx);
                Assert.Equal("unpaid", row.Status);
                Assert.Equal(0m, row.Paid);
                Assert.Equal(25500m, row.Balance);
            }
        }

        [Fact]
        public async Task InsertArInvoice_WithoutDueDate_StoresNull()
        {
            var ar = NewAr();
            ar.DueDate = null;
            var (connection, tx) = Begin();
            using (connection) using (tx)
            {
                var id = await _arRepository.InsertArInvoice(ar, tx);

                var due = await connection.ExecuteScalarAsync<DateTime?>(
                    "SELECT due_date FROM ar_invoice WHERE id = @Id", new { Id = id }, tx);
                Assert.Null(due);
            }
        }

        [Fact]
        public async Task InsertArInvoice_NotCommitted_LeavesNoRow()
        {
            var (connection, tx) = Begin();
            using (connection)
            {
                using (tx)
                {
                    await _arRepository.InsertArInvoice(NewAr(), tx);
                    tx.Rollback();
                }

                var count = await connection.ExecuteScalarAsync<int>(
                    "SELECT COUNT(*) FROM ar_invoice WHERE invoice_id = @Id", new { Id = _invoiceId });
                Assert.Equal(0, count);
            }
        }

        [Fact]
        public async Task InsertArInvoice_SecondForTheSameInvoice_IsRejected()
        {
            var (connection, tx) = Begin();
            using (connection) using (tx)
            {
                await _arRepository.InsertArInvoice(NewAr(), tx);

                await Assert.ThrowsAnyAsync<Exception>(() => _arRepository.InsertArInvoice(NewAr(), tx));
            }
        }

        [Fact]
        public async Task InsertArInvoice_UnknownInvoice_IsRejected()
        {
            var ar = NewAr();
            ar.InvoiceId = long.MaxValue;
            var (connection, tx) = Begin();
            using (connection) using (tx)
            {
                await Assert.ThrowsAnyAsync<Exception>(() => _arRepository.InsertArInvoice(ar, tx));
            }
        }

        // ---- InsertBankReceipt ----

        [Fact]
        public async Task InsertBankReceipt_StoresEveryColumn()
        {
            var (connection, tx) = Begin();
            using (connection) using (tx)
            {
                var (accountId, transactionId) = await InsertTransactionAsync(tx);

                var id = await _receiptRepository.InsertBankReceipt(NewReceipt(transactionId, accountId), tx);

                Assert.True(id > 0);
                var row = await connection.QuerySingleAsync<ReceiptRow>(
                    @"SELECT transaction_id, bank_account_id, customer_id, sales_invoice_id, ar_invoice_id, receipt_number,
                             receipt_date, payment_method, reference, amount, status, created_by
                      FROM bank_receipt WHERE id = @Id", new { Id = id }, tx);
                Assert.Equal(transactionId, row.Transaction_Id);
                Assert.Equal(accountId, row.Bank_Account_Id);
                Assert.Equal(_customerId, row.Customer_Id);
                Assert.Equal(_invoiceId, row.Sales_Invoice_Id);
                Assert.Null(row.Ar_Invoice_Id);
                Assert.Equal(new DateTime(2026, 11, 8), row.Receipt_Date);
                Assert.Equal("Card", row.Payment_Method);
                Assert.Equal("WIRE-77", row.Reference);
                Assert.Equal(10000m, row.Amount);
                Assert.Equal("Posted", row.Status);
                Assert.Equal("system", row.Created_By);
            }
        }

        [Fact]
        public async Task InsertBankReceipt_NumberIsRctYearPlusIdPaddedToSixDigits()
        {
            var (connection, tx) = Begin();
            using (connection) using (tx)
            {
                var (accountId, transactionId) = await InsertTransactionAsync(tx);

                var id = await _receiptRepository.InsertBankReceipt(NewReceipt(transactionId, accountId), tx);

                var number = await connection.ExecuteScalarAsync<string>(
                    "SELECT receipt_number FROM bank_receipt WHERE id = @Id", new { Id = id }, tx);
                Assert.Equal($"RCT-2026-{id:D6}", number);
            }
        }

        [Fact]
        public async Task InsertBankReceipt_WithArInvoice_LinksBothTheSalesInvoiceAndTheArInvoice()
        {
            var (connection, tx) = Begin();
            using (connection) using (tx)
            {
                var (accountId, transactionId) = await InsertTransactionAsync(tx);
                var arId = await _arRepository.InsertArInvoice(NewAr(), tx);

                var id = await _receiptRepository.InsertBankReceipt(NewReceipt(transactionId, accountId, arId), tx);

                var row = await connection.QuerySingleAsync<ReceiptRow>(
                    @"SELECT transaction_id, bank_account_id, customer_id, sales_invoice_id, ar_invoice_id, receipt_number,
                             receipt_date, payment_method, reference, amount, status, created_by
                      FROM bank_receipt WHERE id = @Id", new { Id = id }, tx);
                Assert.Equal(_invoiceId, row.Sales_Invoice_Id);
                Assert.Equal(arId, row.Ar_Invoice_Id);
            }
        }

        [Fact]
        public async Task InsertBankReceipt_WithoutReference_StoresNull()
        {
            var (connection, tx) = Begin();
            using (connection) using (tx)
            {
                var (accountId, transactionId) = await InsertTransactionAsync(tx);

                var id = await _receiptRepository.InsertBankReceipt(NewReceipt(transactionId, accountId, reference: null), tx);

                var reference = await connection.ExecuteScalarAsync<string?>(
                    "SELECT reference FROM bank_receipt WHERE id = @Id", new { Id = id }, tx);
                Assert.Null(reference);
            }
        }

        [Theory]
        [InlineData(PaymentMethod.Cash, "Cash")]
        [InlineData(PaymentMethod.Card, "Card")]
        [InlineData(PaymentMethod.Transfer, "Transfer")]
        [InlineData(PaymentMethod.Check, "Check")]
        public async Task InsertBankReceipt_StoresThePaymentMethodName(PaymentMethod method, string expected)
        {
            var (connection, tx) = Begin();
            using (connection) using (tx)
            {
                var (accountId, transactionId) = await InsertTransactionAsync(tx);

                var id = await _receiptRepository.InsertBankReceipt(NewReceipt(transactionId, accountId, method: method), tx);

                var stored = await connection.ExecuteScalarAsync<string>(
                    "SELECT payment_method FROM bank_receipt WHERE id = @Id", new { Id = id }, tx);
                Assert.Equal(expected, stored);
            }
        }

        [Fact]
        public async Task InsertBankReceipt_NotCommitted_LeavesNoRow()
        {
            var (connection, tx) = Begin();
            using (connection)
            {
                using (tx)
                {
                    var (accountId, transactionId) = await InsertTransactionAsync(tx);
                    await _receiptRepository.InsertBankReceipt(NewReceipt(transactionId, accountId), tx);
                    tx.Rollback();
                }

                var count = await connection.ExecuteScalarAsync<int>(
                    "SELECT COUNT(*) FROM bank_receipt WHERE sales_invoice_id = @Id", new { Id = _invoiceId });
                Assert.Equal(0, count);
            }
        }

        [Fact]
        public async Task InsertBankReceipt_SecondReceiptForTheSameTransaction_IsRejected()
        {
            var (connection, tx) = Begin();
            using (connection) using (tx)
            {
                var (accountId, transactionId) = await InsertTransactionAsync(tx);
                await _receiptRepository.InsertBankReceipt(NewReceipt(transactionId, accountId), tx);

                await Assert.ThrowsAnyAsync<Exception>(
                    () => _receiptRepository.InsertBankReceipt(NewReceipt(transactionId, accountId), tx));
            }
        }

        [Fact]
        public async Task InsertBankReceipt_UnknownTransaction_IsRejected()
        {
            var (connection, tx) = Begin();
            using (connection) using (tx)
            {
                var (accountId, _) = await InsertTransactionAsync(tx);

                await Assert.ThrowsAnyAsync<Exception>(
                    () => _receiptRepository.InsertBankReceipt(NewReceipt(long.MaxValue, accountId), tx));
            }
        }
    }
}
