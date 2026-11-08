using System.Data;
using Dapper;
using NPMotors.Repository.Models;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.Models.Enums;
using NPMotors.Repository.Repositories;

namespace NPMotors.Repository.Tests.DataAccess
{
    // Story 18: CustomerRepository.GetOpenArBalances / GetPostedInvoiceTotals / UpdateCustomerTotals
    // and ArInvoiceRepository.CloseArInvoice, against the test database.
    // Setup rows are committed (reads use their own connection) and removed by the marker sweep in DisposeAsync;
    // the update tests run inside a transaction that is rolled back.
    public class CustomerBalanceRepositoryTest : TestBase, IAsyncLifetime
    {
        private readonly CustomerRepository _customerRepository;
        private readonly ArInvoiceRepository _arRepository;
        private readonly SalesInvoiceRepository _invoiceRepository;
        private readonly string _marker = $"itest-{Guid.NewGuid():N}".Substring(0, 12);
        private int _vehicleCount;
        private long _customerId;
        private long _otherCustomerId;
        private long _salespersonId;
        private long _vendorId;

        public CustomerBalanceRepositoryTest()
        {
            _customerRepository = new CustomerRepository(_repositoryContext);
            _arRepository = new ArInvoiceRepository(_repositoryContext);
            _invoiceRepository = new SalesInvoiceRepository(_repositoryContext);
        }

        public async Task InitializeAsync()
        {
            _customerId = await CreateCustomerAsync("Buyer");
            _otherCustomerId = await CreateCustomerAsync("Other");
            _salespersonId = (await new EmployeeRepository(_repositoryContext).AddEmployee(new EmployeeCreateDto
            {
                First_Name = _marker, Last_Name = "Seller", Department = "Sales", Commission_Rate = 5m, Status = Status.Active
            })).Id;
            _vendorId = (await new VendorRepository(_repositoryContext).AddVendor(new VendorCreateDto
            {
                Name = $"{_marker} Vendor", Type = VendorType.Vehicles, Status = Status.Active
            })).Id;
        }

        public async Task DisposeAsync()
        {
            using IDbConnection connection = _repositoryContext.GetDatabaseConnection();
            var pattern = new { Pattern = _marker + "%", VinPattern = _marker.ToUpperInvariant() + "%" };
            const string markerVehicles =
                "SELECT id FROM vehicle WHERE vendor_id IN (SELECT id FROM vendor WHERE name LIKE @Pattern) OR vin LIKE @VinPattern";
            const string markerInvoices =
                "SELECT id FROM sales_invoice WHERE vehicle_id IN (" + markerVehicles + ") OR customer_id IN (SELECT id FROM customer WHERE first_name LIKE @Pattern)";

            await connection.ExecuteAsync($"DELETE FROM ar_invoice WHERE invoice_id IN ({markerInvoices})", pattern);
            await connection.ExecuteAsync($"UPDATE vehicle SET invoice_id = NULL WHERE id IN ({markerVehicles})", pattern);
            await connection.ExecuteAsync($"DELETE FROM sales_invoice WHERE id IN ({markerInvoices})", pattern);
            await connection.ExecuteAsync($"DELETE FROM vehicle WHERE id IN ({markerVehicles})", pattern);
            await connection.ExecuteAsync("DELETE FROM vendor WHERE name LIKE @Pattern", pattern);
            await connection.ExecuteAsync("DELETE FROM customer WHERE first_name LIKE @Pattern", pattern);
            await connection.ExecuteAsync("DELETE FROM employee WHERE first_name LIKE @Pattern", pattern);
        }

        private async Task<long> CreateCustomerAsync(string lastName) =>
            (await _customerRepository.AddCustomer(new CustomerCreateDto
            {
                First_Name = _marker, Last_Name = lastName, Email = $"{_marker}.{lastName}@example.com", Status = Status.Active
            })).Id;

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

        // Total = 18000 + 1000 + 200 - 700 = 18500.
        private async Task<SalesInvoiceCreateDto> InvoiceDtoAsync(long customerId) => new()
        {
            Customer_Id = customerId, Salesperson_Id = _salespersonId, Vehicle_Id = await CreateVehicleAsync(),
            Invoice_Date = new DateTime(2026, 5, 1), Due_Date = new DateTime(2026, 5, 31),
            Tax = 1000m, Fees = 200m, Discount = 700m
        };

        private async Task<long> PostInvoiceAsync(long customerId) =>
            (await _invoiceRepository.PostSalesInvoice(await InvoiceDtoAsync(customerId))).Id;

        private async Task<long> AddDraftAsync(long customerId) =>
            (await _invoiceRepository.AddSalesInvoice(await InvoiceDtoAsync(customerId))).Id;

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

        private static (IDbConnection Connection, IDbTransaction Tx) Begin()
        {
            var connection = _repositoryContext.GetDatabaseConnection();
            if (connection.State != ConnectionState.Open)
                connection.Open();
            return (connection, connection.BeginTransaction());
        }

        private async Task<IReadOnlyList<decimal>> OpenArBalancesAsync(long customerId)
        {
            var (connection, tx) = Begin();
            using (connection) using (tx)
                return (await _customerRepository.GetOpenArBalances(customerId, tx)).OrderBy(b => b).ToList();
        }

        private async Task<IReadOnlyList<decimal>> PostedTotalsAsync(long customerId)
        {
            var (connection, tx) = Begin();
            using (connection) using (tx)
                return (await _customerRepository.GetPostedInvoiceTotals(customerId, tx)).OrderBy(t => t).ToList();
        }

        private class ArRow
        {
            public decimal Amount { get; set; }
            public decimal Paid { get; set; }
            public decimal Balance { get; set; }
        }

        // ---- GetOpenArBalances (AC 1) ----

        [Fact]
        public async Task GetOpenArBalances_ReturnsTheBalanceOfEachOpenArInvoice()
        {
            await InsertArAsync(await PostInvoiceAsync(_customerId), 18500m, 5000m);
            await InsertArAsync(await PostInvoiceAsync(_customerId), 18500m, 0m);

            var balances = await OpenArBalancesAsync(_customerId);

            Assert.Equal(new[] { 13500m, 18500m }, balances);
        }

        [Fact]
        public async Task GetOpenArBalances_ExcludesArInvoicesWithZeroBalance()
        {
            await InsertArAsync(await PostInvoiceAsync(_customerId), 18500m, 18500m);
            await InsertArAsync(await PostInvoiceAsync(_customerId), 18500m, 8500m);

            var balances = await OpenArBalancesAsync(_customerId);

            Assert.Equal(new[] { 10000m }, balances);
        }

        [Fact]
        public async Task GetOpenArBalances_ExcludesOtherCustomers()
        {
            await InsertArAsync(await PostInvoiceAsync(_customerId), 18500m, 0m);
            await InsertArAsync(await PostInvoiceAsync(_otherCustomerId), 18500m, 10000m);

            Assert.Equal(new[] { 18500m }, await OpenArBalancesAsync(_customerId));
            Assert.Equal(new[] { 8500m }, await OpenArBalancesAsync(_otherCustomerId));
        }

        [Fact]
        public async Task GetOpenArBalances_CustomerWithoutAr_ReturnsEmpty()
        {
            await PostInvoiceAsync(_customerId);

            var balances = await OpenArBalancesAsync(_customerId);

            Assert.Empty(balances);
        }

        [Fact]
        public async Task GetOpenArBalances_UnknownCustomer_ReturnsEmpty()
        {
            var balances = await OpenArBalancesAsync(long.MaxValue);

            Assert.Empty(balances);
        }

        [Fact]
        public async Task GetOpenArBalances_SeesRowsChangedInsideTheSameTransactionBeforeCommit()
        {
            var arId = await InsertArAsync(await PostInvoiceAsync(_customerId), 18500m, 0m);

            var (connection, tx) = Begin();
            using (connection) using (tx)
            {
                await connection.ExecuteAsync("UPDATE ar_invoice SET balance = 0, paid = amount WHERE id = @Id", new { Id = arId }, tx);

                var balances = (await _customerRepository.GetOpenArBalances(_customerId, tx)).ToList();

                Assert.Empty(balances);
            }
        }

        // ---- GetPostedInvoiceTotals (AC 2) ----

        [Fact]
        public async Task GetPostedInvoiceTotals_ReturnsTheTotalOfEachPostedInvoice()
        {
            await PostInvoiceAsync(_customerId);
            await PostInvoiceAsync(_customerId);

            var totals = await PostedTotalsAsync(_customerId);

            Assert.Equal(new[] { 18500m, 18500m }, totals);
        }

        [Fact]
        public async Task GetPostedInvoiceTotals_ExcludesDraftInvoices()
        {
            await PostInvoiceAsync(_customerId);
            await AddDraftAsync(_customerId);

            var totals = await PostedTotalsAsync(_customerId);

            Assert.Equal(new[] { 18500m }, totals);
        }

        [Fact]
        public async Task GetPostedInvoiceTotals_ExcludesCancelledInvoices()
        {
            var cancelledId = await PostInvoiceAsync(_customerId);
            await PostInvoiceAsync(_customerId);
            await _invoiceRepository.CancelCommittedAsync(cancelledId, new SalesInvoiceCancelDto { Reason = "itest" });

            var totals = await PostedTotalsAsync(_customerId);

            Assert.Equal(new[] { 18500m }, totals);
        }

        [Fact]
        public async Task GetPostedInvoiceTotals_ExcludesOtherCustomers()
        {
            await PostInvoiceAsync(_customerId);
            await PostInvoiceAsync(_otherCustomerId);
            await PostInvoiceAsync(_otherCustomerId);

            Assert.Single(await PostedTotalsAsync(_customerId));
            Assert.Equal(2, (await PostedTotalsAsync(_otherCustomerId)).Count);
        }

        [Fact]
        public async Task GetPostedInvoiceTotals_OnlyDraftOrCancelled_ReturnsEmpty()
        {
            var cancelledId = await PostInvoiceAsync(_customerId);
            await _invoiceRepository.CancelCommittedAsync(cancelledId, new SalesInvoiceCancelDto { Reason = "itest" });
            await AddDraftAsync(_customerId);

            var totals = await PostedTotalsAsync(_customerId);

            Assert.Empty(totals);
        }

        [Fact]
        public async Task GetPostedInvoiceTotals_UnknownCustomer_ReturnsEmpty()
        {
            var totals = await PostedTotalsAsync(long.MaxValue);

            Assert.Empty(totals);
        }

        // ---- UpdateCustomerTotals (AC 3) ----

        [Fact]
        public async Task UpdateCustomerTotals_SetsBalanceAndTotalPurchasesOfThatCustomer()
        {
            var (connection, tx) = Begin();
            using (connection) using (tx)
            {
                var rows = await _customerRepository.UpdateCustomerTotals(_customerId, 4000.50m, 20000.25m, tx);

                Assert.Equal(1, rows);
                var row = await connection.QuerySingleAsync<(decimal Balance, decimal TotalPurchases)>(
                    "SELECT balance, total_purchases AS TotalPurchases FROM customer WHERE id = @Id", new { Id = _customerId }, tx);
                Assert.Equal(4000.50m, row.Balance);
                Assert.Equal(20000.25m, row.TotalPurchases);
            }
        }

        [Fact]
        public async Task UpdateCustomerTotals_DoesNotTouchOtherCustomers()
        {
            var (connection, tx) = Begin();
            using (connection) using (tx)
            {
                await _customerRepository.UpdateCustomerTotals(_customerId, 100m, 200m, tx);

                var row = await connection.QuerySingleAsync<(decimal Balance, decimal TotalPurchases)>(
                    "SELECT balance, total_purchases AS TotalPurchases FROM customer WHERE id = @Id", new { Id = _otherCustomerId }, tx);
                Assert.Equal(0m, row.Balance);
                Assert.Equal(0m, row.TotalPurchases);
            }
        }

        [Fact]
        public async Task UpdateCustomerTotals_OverwritesEarlierValuesInsteadOfAddingToThem()
        {
            var (connection, tx) = Begin();
            using (connection) using (tx)
            {
                await _customerRepository.UpdateCustomerTotals(_customerId, 500m, 900m, tx);
                await _customerRepository.UpdateCustomerTotals(_customerId, 300m, 900m, tx);

                var row = await connection.QuerySingleAsync<(decimal Balance, decimal TotalPurchases)>(
                    "SELECT balance, total_purchases AS TotalPurchases FROM customer WHERE id = @Id", new { Id = _customerId }, tx);
                Assert.Equal(300m, row.Balance);
                Assert.Equal(900m, row.TotalPurchases);
            }
        }

        [Fact]
        public async Task UpdateCustomerTotals_UnknownCustomer_UpdatesNothing()
        {
            var (connection, tx) = Begin();
            using (connection) using (tx)
            {
                var rows = await _customerRepository.UpdateCustomerTotals(long.MaxValue, 100m, 200m, tx);

                Assert.Equal(0, rows);
            }
        }

        [Fact]
        public async Task UpdateCustomerTotals_RolledBack_LeavesTheStoredTotalsUnchanged()
        {
            var (connection, tx) = Begin();
            using (connection)
            {
                await _customerRepository.UpdateCustomerTotals(_customerId, 777m, 888m, tx);
                tx.Rollback();
                tx.Dispose();
            }

            var customer = await _customerRepository.GetCustomerById(_customerId);
            Assert.Equal(0m, customer!.Balance);
            Assert.Equal(0m, customer.TotalPurchases);
        }

        [Fact]
        public async Task UpdateCustomerTotals_Committed_IsVisibleThroughGetCustomerById()
        {
            var (connection, tx) = Begin();
            using (connection)
            {
                await _customerRepository.UpdateCustomerTotals(_customerId, 1234.56m, 7890.12m, tx);
                tx.Commit();
                tx.Dispose();
            }

            var customer = await _customerRepository.GetCustomerById(_customerId);
            Assert.Equal(1234.56m, customer!.Balance);
            Assert.Equal(7890.12m, customer.TotalPurchases);
        }

        // ---- Whole recalculation inputs (AC 1-3) ----

        [Fact]
        public async Task OpenArAndPostedTotals_AfterCancellingACreditSale_NoLongerCountTheCancelledInvoice()
        {
            var keptId = await PostInvoiceAsync(_customerId);
            await InsertArAsync(keptId, 18500m, 0m);
            var cancelledId = await PostInvoiceAsync(_customerId);
            await InsertArAsync(cancelledId, 18500m, 0m);

            var (connection, tx) = Begin();
            using (connection) using (tx)
            {
                await _invoiceRepository.CancelSalesInvoice(cancelledId, new SalesInvoiceCancelDto { Reason = "itest" }, tx);
                await _arRepository.CloseArInvoice(cancelledId, tx);

                var balances = (await _customerRepository.GetOpenArBalances(_customerId, tx)).ToList();
                var totals = (await _customerRepository.GetPostedInvoiceTotals(_customerId, tx)).ToList();

                Assert.Equal(new[] { 18500m }, balances);
                Assert.Equal(new[] { 18500m }, totals);
            }
        }

        // ---- CloseArInvoice ----

        [Fact]
        public async Task CloseArInvoice_SetsBalanceToZeroAndKeepsAmountAndPaid()
        {
            var invoiceId = await PostInvoiceAsync(_customerId);
            var arId = await InsertArAsync(invoiceId, 18500m, 0m);

            var (connection, tx) = Begin();
            using (connection) using (tx)
            {
                var rows = await _arRepository.CloseArInvoice(invoiceId, tx);

                Assert.Equal(1, rows);
                var row = await connection.QuerySingleAsync<ArRow>(
                    "SELECT amount, paid, balance FROM ar_invoice WHERE id = @Id", new { Id = arId }, tx);
                Assert.Equal(18500m, row.Amount);
                Assert.Equal(0m, row.Paid);
                Assert.Equal(0m, row.Balance);
            }
        }

        [Fact]
        public async Task CloseArInvoice_InvoiceWithoutAr_UpdatesNothing()
        {
            var invoiceId = await PostInvoiceAsync(_customerId);

            var (connection, tx) = Begin();
            using (connection) using (tx)
            {
                var rows = await _arRepository.CloseArInvoice(invoiceId, tx);

                Assert.Equal(0, rows);
            }
        }

        [Fact]
        public async Task CloseArInvoice_OnlyClosesTheArOfThatInvoice()
        {
            var firstId = await PostInvoiceAsync(_customerId);
            await InsertArAsync(firstId, 18500m, 0m);
            var secondId = await PostInvoiceAsync(_customerId);
            var secondArId = await InsertArAsync(secondId, 18500m, 5000m);

            var (connection, tx) = Begin();
            using (connection) using (tx)
            {
                await _arRepository.CloseArInvoice(firstId, tx);

                var row = await connection.QuerySingleAsync<ArRow>(
                    "SELECT amount, paid, balance FROM ar_invoice WHERE id = @Id", new { Id = secondArId }, tx);
                Assert.Equal(13500m, row.Balance);
            }
        }

        [Fact]
        public async Task CloseArInvoice_RolledBack_LeavesTheBalanceUnchanged()
        {
            var invoiceId = await PostInvoiceAsync(_customerId);
            var arId = await InsertArAsync(invoiceId, 18500m, 0m);

            var (connection, tx) = Begin();
            using (connection)
            {
                await _arRepository.CloseArInvoice(invoiceId, tx);
                tx.Rollback();
                tx.Dispose();
            }

            Assert.Equal(18500m, (await _arRepository.GetArInvoiceById(arId))!.Balance);
        }
    }
}
