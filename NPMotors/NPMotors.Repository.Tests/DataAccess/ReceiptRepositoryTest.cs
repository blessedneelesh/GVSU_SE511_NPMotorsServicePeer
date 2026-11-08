using System.Data;
using Dapper;
using NPMotors.Repository.Models;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.Models.Enums;
using NPMotors.Repository.Repositories;

namespace NPMotors.Repository.Tests.DataAccess
{
    // Story 15: ArInvoiceRepository.GetArInvoiceById / UpdateArPayment, SalesInvoiceRepository.UpdateInvoicePayment
    // and BankReceiptRepository.GetReceiptsByArInvoiceId, against the test database.
    // Setup rows are committed (the reads use their own connection) and removed by the marker sweep in DisposeAsync;
    // the update tests run inside a transaction that is rolled back.
    public class ReceiptRepositoryTest : TestBase, IAsyncLifetime
    {
        private readonly ArInvoiceRepository _arRepository;
        private readonly BankReceiptRepository _receiptRepository;
        private readonly SalesInvoiceRepository _invoiceRepository;
        private readonly string _marker = $"itest-{Guid.NewGuid():N}".Substring(0, 12);
        private int _vehicleCount;
        private long _bankId;
        private long _accountId;
        private long _secondAccountId;
        private long _customerId;
        private long _salespersonId;
        private long _vendorId;
        private long _invoiceId;
        private long _arId;

        public ReceiptRepositoryTest()
        {
            _arRepository = new ArInvoiceRepository(_repositoryContext);
            _receiptRepository = new BankReceiptRepository(_repositoryContext);
            _invoiceRepository = new SalesInvoiceRepository(_repositoryContext);
        }

        private class ArRow
        {
            public decimal Paid { get; set; }
            public decimal Balance { get; set; }
            public string Status { get; set; } = "";
        }

        private class InvoiceRow
        {
            public decimal Paid { get; set; }
            public decimal Balance { get; set; }
            public string? Payment_Status { get; set; }
        }

        public async Task InitializeAsync()
        {
            using IDbConnection connection = _repositoryContext.GetDatabaseConnection();
            _bankId = await connection.ExecuteScalarAsync<long>(
                "INSERT INTO bank (name) VALUES (@Name) RETURNING id", new { Name = $"{_marker} Bank" });
            _accountId = await InsertAccountAsync(connection, "Operating");
            _secondAccountId = await InsertAccountAsync(connection, "Savings");

            _customerId = (await new CustomerRepository(_repositoryContext).AddCustomer(new CustomerCreateDto
            {
                First_Name = _marker, Last_Name = "Buyer", Email = $"{_marker}@example.com", Status = Status.Active
            })).Id;
            _salespersonId = (await new EmployeeRepository(_repositoryContext).AddEmployee(new EmployeeCreateDto
            {
                First_Name = _marker, Last_Name = "Seller", Department = "Sales", Commission_Rate = 5m, Status = Status.Active
            })).Id;
            _vendorId = (await new VendorRepository(_repositoryContext).AddVendor(new VendorCreateDto
            {
                Name = $"{_marker} Vendor", Type = VendorType.Vehicles, Status = Status.Active
            })).Id;

            // Posted invoice: total 18000 + 1000 + 200 - 700 = 18500, paid 0.
            _invoiceId = (await PostInvoiceAsync()).Id;
            _arId = await InsertArAsync(_invoiceId, 18500m, 5000m);
        }

        public async Task DisposeAsync()
        {
            using IDbConnection connection = _repositoryContext.GetDatabaseConnection();
            var pattern = new { Pattern = _marker + "%", VinPattern = _marker.ToUpperInvariant() + "%", BankId = _bankId };
            const string markerVehicles =
                "SELECT id FROM vehicle WHERE vendor_id IN (SELECT id FROM vendor WHERE name LIKE @Pattern) OR vin LIKE @VinPattern";
            const string markerInvoices =
                "SELECT id FROM sales_invoice WHERE vehicle_id IN (" + markerVehicles + ") OR customer_id IN (SELECT id FROM customer WHERE first_name LIKE @Pattern)";
            const string markerAccounts = "SELECT id FROM bank_account WHERE bank_id = @BankId";

            await connection.ExecuteAsync($"DELETE FROM bank_receipt WHERE bank_account_id IN ({markerAccounts})", pattern);
            await connection.ExecuteAsync($@"DELETE FROM ""transaction"" WHERE bank_account_id IN ({markerAccounts})", pattern);
            await connection.ExecuteAsync($"DELETE FROM ar_invoice WHERE invoice_id IN ({markerInvoices})", pattern);
            await connection.ExecuteAsync($"UPDATE vehicle SET invoice_id = NULL WHERE id IN ({markerVehicles})", pattern);
            await connection.ExecuteAsync($"DELETE FROM sales_invoice WHERE id IN ({markerInvoices})", pattern);
            await connection.ExecuteAsync($"DELETE FROM vehicle WHERE id IN ({markerVehicles})", pattern);
            await connection.ExecuteAsync("DELETE FROM vendor WHERE name LIKE @Pattern", pattern);
            await connection.ExecuteAsync("DELETE FROM customer WHERE first_name LIKE @Pattern", pattern);
            await connection.ExecuteAsync("DELETE FROM employee WHERE first_name LIKE @Pattern", pattern);
            await connection.ExecuteAsync("DELETE FROM bank_account WHERE bank_id = @BankId", pattern);
            await connection.ExecuteAsync("DELETE FROM bank WHERE id = @BankId", pattern);
        }

        private async Task<long> InsertAccountAsync(IDbConnection connection, string name) =>
            await connection.ExecuteScalarAsync<long>(
                @"INSERT INTO bank_account (bank_id, name, type, opening_balance, book_balance, status)
                  VALUES (@BankId, @Name, 'Checking', 0, 0, 'active') RETURNING id",
                new { BankId = _bankId, Name = $"{_marker} {name}" });

        private async Task<long> CreateVehicleAsync()
        {
            var vehicle = await new VehicleRepository(_repositoryContext).AddVehicle(new VehicleCreateDto
            {
                Vendor_Id = _vendorId,
                Vin = $"{_marker}{++_vehicleCount}".ToUpperInvariant().PadRight(17, '0').Substring(0, 17),
                Make = "Honda", Model = "Civic",
                Purchase_Price = 15000m, Additional_Costs = 250m, Selling_Price = 18000m
            });
            return vehicle.Id;
        }

        private SalesInvoiceCreateDto InvoiceDto(long vehicleId) => new()
        {
            Customer_Id = _customerId, Salesperson_Id = _salespersonId, Vehicle_Id = vehicleId,
            Invoice_Date = new DateTime(2026, 5, 1), Due_Date = new DateTime(2026, 5, 31),
            Tax = 1000m, Fees = 200m, Discount = 700m
        };

        private async Task<SalesInvoiceResponseDto> PostInvoiceAsync() =>
            await _invoiceRepository.PostSalesInvoice(InvoiceDto(await CreateVehicleAsync()));

        private async Task<long> InsertArAsync(long invoiceId, decimal amount, decimal paid)
        {
            using IDbConnection connection = _repositoryContext.GetDatabaseConnection();
            using IDbTransaction tx = connection.BeginTransaction();
            var id = await _arRepository.InsertArInvoice(new ArInvoice
            {
                InvoiceId = invoiceId,
                Date = new DateTime(2026, 5, 2),
                DueDate = new DateTime(2026, 5, 31),
                Amount = amount,
                Paid = paid,
                Balance = amount - paid,
                Status = PaymentStatus.Partial,
                CreatedAt = new DateTime(2026, 5, 2),
                CreatedBy = "system"
            }, tx);
            tx.Commit();
            return id;
        }

        private async Task<long> InsertReceiptAsync(DateTime receiptDate, decimal amount, long accountId,
            long? arInvoiceId, PaymentMethod method = PaymentMethod.Card, string? reference = "REF-1")
        {
            using IDbConnection connection = _repositoryContext.GetDatabaseConnection();
            using IDbTransaction tx = connection.BeginTransaction();
            var transactionId = await connection.ExecuteScalarAsync<long>(
                @"INSERT INTO ""transaction"" (bank_account_id, type, direction, number, date, amount, status, source_number, created_by)
                  VALUES (@AccountId, 'BankReceipt', 'in'::direction_enum, @Number, CURRENT_DATE, @Amount, 'posted', 'INV-TEST', 'system')
                  RETURNING id",
                new { AccountId = accountId, Amount = amount, Number = $"{_marker}-{Guid.NewGuid():N}".Substring(0, 30) }, tx);
            var id = await _receiptRepository.InsertBankReceipt(new BankReceipt
            {
                TransactionId = transactionId,
                BankAccountId = accountId,
                CustomerId = _customerId,
                SalesInvoiceId = _invoiceId,
                ArInvoiceId = arInvoiceId,
                ReceiptDate = receiptDate,
                PaymentMethod = method,
                Reference = reference,
                Amount = amount,
                Status = "Posted",
                CreatedAt = new DateTime(2026, 11, 8),
                CreatedBy = "system"
            }, tx);
            tx.Commit();
            return id;
        }

        private static (IDbConnection Connection, IDbTransaction Tx) Begin()
        {
            var connection = _repositoryContext.GetDatabaseConnection();
            if (connection.State != ConnectionState.Open)
                connection.Open();
            return (connection, connection.BeginTransaction());
        }

        // ---- GetArInvoiceById ----

        [Fact]
        public async Task GetArInvoiceById_Existing_ReturnsStoredColumns()
        {
            var ar = await _arRepository.GetArInvoiceById(_arId);

            Assert.NotNull(ar);
            Assert.Equal(_arId, ar!.Id);
            Assert.Equal(_invoiceId, ar.InvoiceId);
            Assert.Equal($"AR-{_arId:D6}", ar.Number);
            Assert.Equal(new DateTime(2026, 5, 2), ar.Date);
            Assert.Equal(new DateTime(2026, 5, 31), ar.DueDate);
            Assert.Equal(18500m, ar.Amount);
            Assert.Equal(5000m, ar.Paid);
            Assert.Equal(13500m, ar.Balance);
            Assert.Equal(PaymentStatus.Partial, ar.Status);
            Assert.Equal("system", ar.CreatedBy);
        }

        [Fact]
        public async Task GetArInvoiceById_Existing_IncludesInvoiceNumberAndCustomerNameFromJoins()
        {
            var ar = await _arRepository.GetArInvoiceById(_arId);

            Assert.Equal($"INV-{_invoiceId:D6}", ar!.InvoiceNumber);
            Assert.Equal($"{_marker} Buyer", ar.CustomerName);
        }

        [Fact]
        public async Task GetArInvoiceById_Unknown_ReturnsNull()
        {
            var ar = await _arRepository.GetArInvoiceById(long.MaxValue);

            Assert.Null(ar);
        }

        // ---- UpdateArPayment (AC 4) ----

        [Fact]
        public async Task UpdateArPayment_BalanceMatches_UpdatesPaidBalanceAndStatus()
        {
            var (connection, tx) = Begin();
            using (connection) using (tx)
            {
                var rows = await _arRepository.UpdateArPayment(
                    new ArInvoice { Id = _arId, Paid = 18500m, Balance = 0m, Status = PaymentStatus.Paid }, 13500m, tx);

                Assert.Equal(1, rows);
                var row = await connection.QuerySingleAsync<ArRow>(
                    "SELECT paid, balance, status FROM ar_invoice WHERE id = @Id", new { Id = _arId }, tx);
                Assert.Equal(18500m, row.Paid);
                Assert.Equal(0m, row.Balance);
                Assert.Equal("paid", row.Status);
            }
        }

        [Fact]
        public async Task UpdateArPayment_BalanceChangedByAnotherPayment_UpdatesNothing()
        {
            var (connection, tx) = Begin();
            using (connection) using (tx)
            {
                var rows = await _arRepository.UpdateArPayment(
                    new ArInvoice { Id = _arId, Paid = 18500m, Balance = 0m, Status = PaymentStatus.Paid }, 9999m, tx);

                Assert.Equal(0, rows);
                var row = await connection.QuerySingleAsync<ArRow>(
                    "SELECT paid, balance, status FROM ar_invoice WHERE id = @Id", new { Id = _arId }, tx);
                Assert.Equal(5000m, row.Paid);
                Assert.Equal(13500m, row.Balance);
                Assert.Equal("partial", row.Status);
            }
        }

        [Fact]
        public async Task UpdateArPayment_SecondUpdateWithTheOldExpectedBalance_UpdatesNothing()
        {
            var (connection, tx) = Begin();
            using (connection) using (tx)
            {
                var first = await _arRepository.UpdateArPayment(
                    new ArInvoice { Id = _arId, Paid = 9000m, Balance = 9500m, Status = PaymentStatus.Partial }, 13500m, tx);
                var second = await _arRepository.UpdateArPayment(
                    new ArInvoice { Id = _arId, Paid = 11000m, Balance = 7500m, Status = PaymentStatus.Partial }, 13500m, tx);

                Assert.Equal(1, first);
                Assert.Equal(0, second);
                var row = await connection.QuerySingleAsync<ArRow>(
                    "SELECT paid, balance, status FROM ar_invoice WHERE id = @Id", new { Id = _arId }, tx);
                Assert.Equal(9000m, row.Paid);
            }
        }

        [Fact]
        public async Task UpdateArPayment_OnlyTouchesTheGivenAr()
        {
            var secondInvoice = await PostInvoiceAsync();
            var secondAr = await InsertArAsync(secondInvoice.Id, 18500m, 0m);
            var (connection, tx) = Begin();
            using (connection) using (tx)
            {
                await _arRepository.UpdateArPayment(
                    new ArInvoice { Id = _arId, Paid = 18500m, Balance = 0m, Status = PaymentStatus.Paid }, 13500m, tx);

                var other = await connection.QuerySingleAsync<ArRow>(
                    "SELECT paid, balance, status FROM ar_invoice WHERE id = @Id", new { Id = secondAr }, tx);
                Assert.Equal(0m, other.Paid);
                Assert.Equal(18500m, other.Balance);
            }
        }

        // ---- UpdateInvoicePayment (AC 4) ----

        [Fact]
        public async Task UpdateInvoicePayment_PostedInvoice_UpdatesPaidBalanceAndPaymentStatus()
        {
            var (connection, tx) = Begin();
            using (connection) using (tx)
            {
                var rows = await _invoiceRepository.UpdateInvoicePayment(
                    new SalesInvoice { Id = _invoiceId, Paid = 5000m, Balance = 13500m, PaymentStatus = PaymentStatus.Partial }, tx);

                Assert.Equal(1, rows);
                var row = await connection.QuerySingleAsync<InvoiceRow>(
                    "SELECT paid, balance, payment_status FROM sales_invoice WHERE id = @Id", new { Id = _invoiceId }, tx);
                Assert.Equal(5000m, row.Paid);
                Assert.Equal(13500m, row.Balance);
                Assert.Equal("partial", row.Payment_Status);
            }
        }

        [Fact]
        public async Task UpdateInvoicePayment_FullPayment_StoresPaid()
        {
            var (connection, tx) = Begin();
            using (connection) using (tx)
            {
                await _invoiceRepository.UpdateInvoicePayment(
                    new SalesInvoice { Id = _invoiceId, Paid = 18500m, Balance = 0m, PaymentStatus = PaymentStatus.Paid }, tx);

                var row = await connection.QuerySingleAsync<InvoiceRow>(
                    "SELECT paid, balance, payment_status FROM sales_invoice WHERE id = @Id", new { Id = _invoiceId }, tx);
                Assert.Equal(18500m, row.Paid);
                Assert.Equal(0m, row.Balance);
                Assert.Equal("paid", row.Payment_Status);
            }
        }

        [Fact]
        public async Task UpdateInvoicePayment_DraftInvoice_UpdatesNothing()
        {
            var draft = await _invoiceRepository.AddSalesInvoice(InvoiceDto(await CreateVehicleAsync()));
            var (connection, tx) = Begin();
            using (connection) using (tx)
            {
                var rows = await _invoiceRepository.UpdateInvoicePayment(
                    new SalesInvoice { Id = draft.Id, Paid = 100m, Balance = 18400m, PaymentStatus = PaymentStatus.Partial }, tx);

                Assert.Equal(0, rows);
                var paid = await connection.ExecuteScalarAsync<decimal>(
                    "SELECT paid FROM sales_invoice WHERE id = @Id", new { Id = draft.Id }, tx);
                Assert.Equal(0m, paid);
            }
        }

        [Fact]
        public async Task UpdateInvoicePayment_CancelledInvoice_UpdatesNothing()
        {
            var posted = await PostInvoiceAsync();
            await _invoiceRepository.CancelCommittedAsync(posted.Id, new SalesInvoiceCancelDto { Reason = "test" });
            var (connection, tx) = Begin();
            using (connection) using (tx)
            {
                var rows = await _invoiceRepository.UpdateInvoicePayment(
                    new SalesInvoice { Id = posted.Id, Paid = 100m, Balance = 18400m, PaymentStatus = PaymentStatus.Partial }, tx);

                Assert.Equal(0, rows);
            }
        }

        [Fact]
        public async Task UpdateInvoicePayment_UnknownInvoice_UpdatesNothing()
        {
            var (connection, tx) = Begin();
            using (connection) using (tx)
            {
                var rows = await _invoiceRepository.UpdateInvoicePayment(
                    new SalesInvoice { Id = long.MaxValue, Paid = 1m, Balance = 1m, PaymentStatus = PaymentStatus.Partial }, tx);

                Assert.Equal(0, rows);
            }
        }

        // ---- GetReceiptsByArInvoiceId (payment history, AC 8) ----

        [Fact]
        public async Task GetReceiptsByArInvoiceId_NoReceipts_ReturnsEmptyNotNull()
        {
            var receipts = await _receiptRepository.GetReceiptsByArInvoiceId(_arId);

            Assert.NotNull(receipts);
            Assert.Empty(receipts);
        }

        [Fact]
        public async Task GetReceiptsByArInvoiceId_Unknown_ReturnsEmpty()
        {
            var receipts = await _receiptRepository.GetReceiptsByArInvoiceId(long.MaxValue);

            Assert.Empty(receipts);
        }

        [Fact]
        public async Task GetReceiptsByArInvoiceId_MapsStoredColumnsAndBankAccountName()
        {
            var id = await InsertReceiptAsync(new DateTime(2026, 6, 1), 2500m, _accountId, _arId, PaymentMethod.Check, "CHK-1001");

            var receipt = Assert.Single(await _receiptRepository.GetReceiptsByArInvoiceId(_arId));

            Assert.Equal(id, receipt.Id);
            Assert.True(receipt.TransactionId > 0);
            Assert.Equal(_accountId, receipt.BankAccountId);
            Assert.Equal(_customerId, receipt.CustomerId);
            Assert.Equal(_invoiceId, receipt.SalesInvoiceId);
            Assert.Equal(_arId, receipt.ArInvoiceId);
            Assert.Equal($"RCT-2026-{id:D6}", receipt.ReceiptNumber);
            Assert.Equal(new DateTime(2026, 6, 1), receipt.ReceiptDate);
            Assert.Equal(PaymentMethod.Check, receipt.PaymentMethod);
            Assert.Equal("CHK-1001", receipt.Reference);
            Assert.Equal(2500m, receipt.Amount);
            Assert.Equal("Posted", receipt.Status);
            Assert.Equal("system", receipt.CreatedBy);
            Assert.Equal($"{_marker} Operating", receipt.BankAccountName);
        }

        [Fact]
        public async Task GetReceiptsByArInvoiceId_NullReference_ReturnsNullReference()
        {
            await InsertReceiptAsync(new DateTime(2026, 6, 1), 100m, _accountId, _arId, reference: null);

            var receipt = Assert.Single(await _receiptRepository.GetReceiptsByArInvoiceId(_arId));

            Assert.Null(receipt.Reference);
        }

        [Fact]
        public async Task GetReceiptsByArInvoiceId_OrderedByReceiptDateThenId()
        {
            var late = await InsertReceiptAsync(new DateTime(2026, 8, 1), 300m, _accountId, _arId);
            var earlyFirst = await InsertReceiptAsync(new DateTime(2026, 6, 1), 100m, _accountId, _arId);
            var earlySecond = await InsertReceiptAsync(new DateTime(2026, 6, 1), 200m, _accountId, _arId);

            var receipts = (await _receiptRepository.GetReceiptsByArInvoiceId(_arId)).ToList();

            Assert.Equal(new[] { earlyFirst, earlySecond, late }, receipts.Select(r => r.Id).ToArray());
        }

        [Fact]
        public async Task GetReceiptsByArInvoiceId_EveryPaymentIsItsOwnRow_NeverMerged()
        {
            await InsertReceiptAsync(new DateTime(2026, 6, 1), 100m, _accountId, _arId);
            await InsertReceiptAsync(new DateTime(2026, 6, 1), 100m, _accountId, _arId);

            var receipts = (await _receiptRepository.GetReceiptsByArInvoiceId(_arId)).ToList();

            Assert.Equal(2, receipts.Count);
            Assert.Equal(2, receipts.Select(r => r.TransactionId).Distinct().Count());
            Assert.Equal(2, receipts.Select(r => r.ReceiptNumber).Distinct().Count());
        }

        [Fact]
        public async Task GetReceiptsByArInvoiceId_OnlyReceiptsLinkedToThatAr()
        {
            var secondInvoice = await PostInvoiceAsync();
            var secondAr = await InsertArAsync(secondInvoice.Id, 18500m, 0m);
            var mine = await InsertReceiptAsync(new DateTime(2026, 6, 1), 100m, _accountId, _arId);
            await InsertReceiptAsync(new DateTime(2026, 6, 2), 200m, _accountId, secondAr);
            await InsertReceiptAsync(new DateTime(2026, 6, 3), 300m, _accountId, null);

            var receipts = (await _receiptRepository.GetReceiptsByArInvoiceId(_arId)).ToList();

            Assert.Equal(mine, Assert.Single(receipts).Id);
        }

        [Fact]
        public async Task GetReceiptsByArInvoiceId_ShowsTheAccountEachReceiptWasDepositedInto()
        {
            await InsertReceiptAsync(new DateTime(2026, 6, 1), 100m, _accountId, _arId);
            await InsertReceiptAsync(new DateTime(2026, 6, 2), 200m, _secondAccountId, _arId);

            var receipts = (await _receiptRepository.GetReceiptsByArInvoiceId(_arId)).ToList();

            Assert.Equal($"{_marker} Operating", receipts[0].BankAccountName);
            Assert.Equal($"{_marker} Savings", receipts[1].BankAccountName);
        }
    }
}
