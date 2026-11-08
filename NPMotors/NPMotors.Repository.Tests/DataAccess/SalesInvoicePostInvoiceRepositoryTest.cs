using System.Data;
using Dapper;
using NPMotors.Repository.Exceptions;
using NPMotors.Repository.Models;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.Models.Enums;
using NPMotors.Repository.Repositories;

namespace NPMotors.Repository.Tests.DataAccess
{
    // Story 14 repository methods: GetInvoiceById, UpdatePostedInvoice (SalesInvoiceRepository) and
    // MarkVehicleSold (VehicleRepository). Writes run inside a transaction that is never committed,
    // so they leave nothing behind; the drafts and their parents are removed by the marker sweep.
    // Draft total: vehicle selling price 25000 + tax 1000 + fees 200 - discount 700 = 25500.
    public class SalesInvoicePostInvoiceRepositoryTest : TestBase, IAsyncLifetime
    {
        private readonly SalesInvoiceRepository _repository;
        private readonly VehicleRepository _vehicleRepository;
        private readonly string _marker = $"itest-{Guid.NewGuid():N}".Substring(0, 12);
        private long _customerId;
        private long _salespersonId;
        private int _vehicleCount;

        private class InvoiceState
        {
            public string Lifecycle { get; set; } = "";
            public string? Payment_Status { get; set; }
            public decimal Paid { get; set; }
            public decimal Balance { get; set; }
        }

        private class VehicleState
        {
            public string Status { get; set; } = "";
            public long? Invoice_Id { get; set; }
        }

        public SalesInvoicePostInvoiceRepositoryTest()
        {
            _repository = new SalesInvoiceRepository(_repositoryContext);
            _vehicleRepository = new VehicleRepository(_repositoryContext);
        }

        public async Task InitializeAsync()
        {
            _customerId = (await new CustomerRepository(_repositoryContext).AddCustomer(new CustomerCreateDto
            {
                First_Name = _marker, Last_Name = "Buyer", Email = $"{_marker}@example.com", Status = Status.Active
            })).Id;
            _salespersonId = (await new EmployeeRepository(_repositoryContext).AddEmployee(new EmployeeCreateDto
            {
                First_Name = _marker, Last_Name = "Seller", Department = "Sales", Commission_Rate = 5m, Status = Status.Active
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

        private async Task<long> CreateVehicleAsync(string status = "available")
        {
            var vendor = await new VendorRepository(_repositoryContext).AddVendor(new VendorCreateDto
            {
                Name = $"{_marker} Vendor", Type = VendorType.Vehicles, Status = Status.Active
            });
            var vehicle = await _vehicleRepository.AddVehicle(new VehicleCreateDto
            {
                Vendor_Id = vendor.Id,
                Vin = $"{_marker}{++_vehicleCount}".ToUpperInvariant().PadRight(17, '0').Substring(0, 17),
                Make = "Toyota", Model = "Camry",
                Purchase_Price = 20000m, Additional_Costs = 500m, Selling_Price = 25000m
            });
            if (status != "available")
            {
                using IDbConnection connection = _repositoryContext.GetDatabaseConnection();
                await connection.ExecuteAsync("UPDATE vehicle SET status = @Status WHERE id = @Id", new { Status = status, Id = vehicle.Id });
            }
            return vehicle.Id;
        }

        private SalesInvoiceCreateDto NewDto(long vehicleId) => new()
        {
            Customer_Id = _customerId,
            Salesperson_Id = _salespersonId,
            Vehicle_Id = vehicleId,
            Invoice_Date = new DateTime(2026, 5, 1),
            Due_Date = new DateTime(2026, 5, 31),
            Tax = 1000m, Fees = 200m, Discount = 700m
        };

        private async Task<(long InvoiceId, long VehicleId)> CreateDraftAsync()
        {
            var vehicleId = await CreateVehicleAsync();
            var draft = await _repository.AddSalesInvoice(NewDto(vehicleId));
            return (draft.Id, vehicleId);
        }

        private static (IDbConnection Connection, IDbTransaction Tx) Begin()
        {
            var connection = _repositoryContext.GetDatabaseConnection();
            if (connection.State != ConnectionState.Open)
                connection.Open();
            return (connection, connection.BeginTransaction());
        }

        private static Task<InvoiceState> InvoiceStateAsync(IDbTransaction tx, long id) =>
            tx.Connection!.QuerySingleAsync<InvoiceState>(
                "SELECT lifecycle, payment_status, paid, balance FROM sales_invoice WHERE id = @Id", new { Id = id }, tx);

        private static Task<VehicleState> VehicleStateAsync(IDbTransaction tx, long id) =>
            tx.Connection!.QuerySingleAsync<VehicleState>(
                "SELECT LOWER(status) AS status, invoice_id FROM vehicle WHERE id = @Id", new { Id = id }, tx);

        // ---- GetInvoiceById ----

        [Fact]
        public async Task GetInvoiceById_Draft_ReturnsStoredColumns()
        {
            var (invoiceId, vehicleId) = await CreateDraftAsync();

            var invoice = await _repository.GetInvoiceById(invoiceId);

            Assert.NotNull(invoice);
            Assert.Equal(invoiceId, invoice!.Id);
            Assert.StartsWith("INV-", invoice.Number);
            Assert.Equal(_customerId, invoice.CustomerId);
            Assert.Equal(vehicleId, invoice.VehicleId);
            Assert.Equal(new DateTime(2026, 5, 31), invoice.DueDate);
            Assert.Equal(25500m, invoice.Total);
            Assert.Equal(0m, invoice.Paid);
            Assert.Equal(25500m, invoice.Balance);
            Assert.Equal("draft", invoice.Lifecycle);
            Assert.Null(invoice.PaymentStatus);
        }

        [Fact]
        public async Task GetInvoiceById_DraftWithoutDueDate_HasNullDueDate()
        {
            var vehicleId = await CreateVehicleAsync();
            var dto = NewDto(vehicleId);
            dto.Due_Date = null;
            var draft = await _repository.AddSalesInvoice(dto);

            var invoice = await _repository.GetInvoiceById(draft.Id);

            Assert.Null(invoice!.DueDate);
        }

        [Fact]
        public async Task GetInvoiceById_Posted_ReturnsPostedLifecycleAndPaymentStatus()
        {
            var vehicleId = await CreateVehicleAsync();
            var posted = await _repository.PostSalesInvoice(NewDto(vehicleId));

            var invoice = await _repository.GetInvoiceById(posted.Id);

            Assert.Equal("posted", invoice!.Lifecycle);
            Assert.Equal(PaymentStatus.Unpaid, invoice.PaymentStatus);
        }

        [Fact]
        public async Task GetInvoiceById_UnknownId_ReturnsNull()
        {
            Assert.Null(await _repository.GetInvoiceById(long.MaxValue));
        }

        // ---- UpdatePostedInvoice ----

        [Fact]
        public async Task UpdatePostedInvoice_Draft_SavesLifecyclePaidBalanceAndStatus()
        {
            var (invoiceId, _) = await CreateDraftAsync();
            var invoice = (await _repository.GetInvoiceById(invoiceId))!;
            invoice.Lifecycle = "posted";
            invoice.Paid = 10000m;
            invoice.Balance = 15500m;
            invoice.PaymentStatus = PaymentStatus.Partial;
            var (connection, tx) = Begin();
            using (connection) using (tx)
            {
                var rows = await _repository.UpdatePostedInvoice(invoice, tx);

                Assert.Equal(1, rows);
                var state = await InvoiceStateAsync(tx, invoiceId);
                Assert.Equal("posted", state.Lifecycle);
                Assert.Equal("partial", state.Payment_Status);
                Assert.Equal(10000m, state.Paid);
                Assert.Equal(15500m, state.Balance);
            }
        }

        [Fact]
        public async Task UpdatePostedInvoice_NotCommitted_LeavesTheDraftUntouched()
        {
            var (invoiceId, _) = await CreateDraftAsync();
            var invoice = (await _repository.GetInvoiceById(invoiceId))!;
            invoice.Lifecycle = "posted";
            invoice.PaymentStatus = PaymentStatus.Unpaid;
            var (connection, tx) = Begin();
            using (connection) using (tx)
            {
                await _repository.UpdatePostedInvoice(invoice, tx);
                tx.Rollback();
            }

            var stored = await _repository.GetInvoiceById(invoiceId);
            Assert.Equal("draft", stored!.Lifecycle);
            Assert.Null(stored.PaymentStatus);
        }

        [Fact]
        public async Task UpdatePostedInvoice_AlreadyPosted_ReturnsZeroAndChangesNothing()
        {
            var vehicleId = await CreateVehicleAsync();
            var posted = await _repository.PostSalesInvoice(NewDto(vehicleId));
            var invoice = (await _repository.GetInvoiceById(posted.Id))!;
            invoice.Paid = 100m;
            invoice.Balance = 25400m;
            invoice.PaymentStatus = PaymentStatus.Partial;
            var (connection, tx) = Begin();
            using (connection) using (tx)
            {
                var rows = await _repository.UpdatePostedInvoice(invoice, tx);

                Assert.Equal(0, rows);
                var state = await InvoiceStateAsync(tx, posted.Id);
                Assert.Equal(0m, state.Paid);
                Assert.Equal("unpaid", state.Payment_Status);
            }
        }

        [Fact]
        public async Task UpdatePostedInvoice_CancelledInvoice_ReturnsZero()
        {
            var vehicleId = await CreateVehicleAsync();
            var posted = await _repository.PostSalesInvoice(NewDto(vehicleId));
            await _repository.CancelCommittedAsync(posted.Id, new SalesInvoiceCancelDto { Reason = "itest" });
            var invoice = (await _repository.GetInvoiceById(posted.Id))!;
            invoice.Lifecycle = "posted";
            var (connection, tx) = Begin();
            using (connection) using (tx)
            {
                Assert.Equal(0, await _repository.UpdatePostedInvoice(invoice, tx));
            }
        }

        [Fact]
        public async Task UpdatePostedInvoice_UnknownId_ReturnsZero()
        {
            var (connection, tx) = Begin();
            using (connection) using (tx)
            {
                var rows = await _repository.UpdatePostedInvoice(
                    new SalesInvoice { Id = long.MaxValue, Lifecycle = "posted", PaymentStatus = PaymentStatus.Unpaid }, tx);

                Assert.Equal(0, rows);
            }
        }

        [Fact]
        public async Task UpdatePostedInvoice_OnlyTheGivenInvoiceChanges()
        {
            var (invoiceId, _) = await CreateDraftAsync();
            var (otherId, _) = await CreateDraftAsync();
            var invoice = (await _repository.GetInvoiceById(invoiceId))!;
            invoice.Lifecycle = "posted";
            invoice.PaymentStatus = PaymentStatus.Unpaid;
            var (connection, tx) = Begin();
            using (connection) using (tx)
            {
                await _repository.UpdatePostedInvoice(invoice, tx);

                Assert.Equal("draft", (await InvoiceStateAsync(tx, otherId)).Lifecycle);
            }
        }

        [Fact]
        public async Task UpdatePostedInvoice_SecondPostedInvoiceForTheSameVehicle_ThrowsConflict()
        {
            var vehicleId = await CreateVehicleAsync();
            var first = await _repository.AddSalesInvoice(NewDto(vehicleId));
            var second = await _repository.AddSalesInvoice(NewDto(vehicleId));
            var firstInvoice = (await _repository.GetInvoiceById(first.Id))!;
            var secondInvoice = (await _repository.GetInvoiceById(second.Id))!;
            firstInvoice.Lifecycle = secondInvoice.Lifecycle = "posted";
            firstInvoice.PaymentStatus = secondInvoice.PaymentStatus = PaymentStatus.Unpaid;
            var (connection, tx) = Begin();
            using (connection) using (tx)
            {
                Assert.Equal(1, await _repository.UpdatePostedInvoice(firstInvoice, tx));

                var ex = await Assert.ThrowsAsync<ConflictException>(() => _repository.UpdatePostedInvoice(secondInvoice, tx));

                Assert.Contains("already sold", ex.Message);
            }
        }

        // ---- MarkVehicleSold ----

        [Fact]
        public async Task MarkVehicleSold_AvailableVehicle_SetsSoldAndLinksTheInvoice()
        {
            var (invoiceId, vehicleId) = await CreateDraftAsync();
            var (connection, tx) = Begin();
            using (connection) using (tx)
            {
                var rows = await _vehicleRepository.MarkVehicleSold(vehicleId, invoiceId, tx);

                Assert.Equal(1, rows);
                var state = await VehicleStateAsync(tx, vehicleId);
                Assert.Equal("sold", state.Status);
                Assert.Equal(invoiceId, state.Invoice_Id);
            }
        }

        [Fact]
        public async Task MarkVehicleSold_NotCommitted_LeavesTheVehicleAvailable()
        {
            var (invoiceId, vehicleId) = await CreateDraftAsync();
            var (connection, tx) = Begin();
            using (connection) using (tx)
            {
                await _vehicleRepository.MarkVehicleSold(vehicleId, invoiceId, tx);
                tx.Rollback();
            }

            var vehicle = await _vehicleRepository.GetVehicleById(vehicleId);
            Assert.Equal(VehicleStatus.Available, vehicle!.Status);
        }

        [Theory]
        [InlineData("sold")]
        [InlineData("reserved")]
        public async Task MarkVehicleSold_VehicleNotAvailable_ReturnsZeroAndChangesNothing(string status)
        {
            var (invoiceId, vehicleId) = await CreateDraftAsync();
            using (IDbConnection direct = _repositoryContext.GetDatabaseConnection())
                await direct.ExecuteAsync("UPDATE vehicle SET status = @Status WHERE id = @Id", new { Status = status, Id = vehicleId });
            var (connection, tx) = Begin();
            using (connection) using (tx)
            {
                var rows = await _vehicleRepository.MarkVehicleSold(vehicleId, invoiceId, tx);

                Assert.Equal(0, rows);
                var state = await VehicleStateAsync(tx, vehicleId);
                Assert.Equal(status, state.Status);
                Assert.Null(state.Invoice_Id);
            }
        }

        [Fact]
        public async Task MarkVehicleSold_UnknownVehicle_ReturnsZero()
        {
            var (invoiceId, _) = await CreateDraftAsync();
            var (connection, tx) = Begin();
            using (connection) using (tx)
            {
                Assert.Equal(0, await _vehicleRepository.MarkVehicleSold(long.MaxValue, invoiceId, tx));
            }
        }

        [Fact]
        public async Task MarkVehicleSold_OnlyTheGivenVehicleChanges()
        {
            var (invoiceId, vehicleId) = await CreateDraftAsync();
            var otherVehicleId = await CreateVehicleAsync();
            var (connection, tx) = Begin();
            using (connection) using (tx)
            {
                await _vehicleRepository.MarkVehicleSold(vehicleId, invoiceId, tx);

                Assert.Equal("available", (await VehicleStateAsync(tx, otherVehicleId)).Status);
            }
        }
    }
}
