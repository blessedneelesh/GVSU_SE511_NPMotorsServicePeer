using System.Data;
using Dapper;
using NPMotors.Repository.Exceptions;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.Models.Enums;
using NPMotors.Repository.RequestFeatures;
using NPMotors.Repository.Repositories;

namespace NPMotors.Repository.Tests.DataAccess
{
    public class SalesInvoiceRepositoryTest : TestBase, IAsyncLifetime
    {
        private readonly SalesInvoiceRepository _repository;
        private readonly VehicleRepository _vehicleRepository;
        private readonly VendorRepository _vendorRepository;
        private readonly CustomerRepository _customerRepository;
        private readonly EmployeeRepository _employeeRepository;
        private readonly string _marker = $"itest-{Guid.NewGuid():N}".Substring(0, 12);
        private readonly List<long> _invoiceIds = new();
        private readonly List<long> _vehicleIds = new();
        private readonly List<long> _vendorIds = new();
        private readonly List<long> _customerIds = new();
        private readonly List<long> _employeeIds = new();

        public SalesInvoiceRepositoryTest()
        {
            _repository = new SalesInvoiceRepository(_repositoryContext);
            _vehicleRepository = new VehicleRepository(_repositoryContext, null!);
            _vendorRepository = new VendorRepository(_repositoryContext);
            _customerRepository = new CustomerRepository(_repositoryContext);
            _employeeRepository = new EmployeeRepository(_repositoryContext);
        }

        public Task InitializeAsync() => Task.CompletedTask;

        public async Task DisposeAsync()
        {
            // Sweep by this run's unique marker (not by tracked ids), so rows a failed test never
            // tracked, such as a second invoice for the same vehicle, are removed too.
            // Order follows the foreign keys: invoices, vehicles, vendors, customers, employees.
            using IDbConnection connection = _repositoryContext.GetDatabaseConnection();
            var pattern = new { Pattern = _marker + "%", VinPattern = _marker.ToUpperInvariant() + "%" };
            const string markerVehicles =
                "SELECT id FROM vehicle WHERE vendor_id IN (SELECT id FROM vendor WHERE name LIKE @Pattern) OR vin LIKE @VinPattern";
            await connection.ExecuteAsync(
                $"UPDATE vehicle SET invoice_id = NULL WHERE id IN ({markerVehicles})", pattern);
            await connection.ExecuteAsync(
                $@"DELETE FROM sales_invoice
                   WHERE vehicle_id IN ({markerVehicles})
                      OR customer_id IN (SELECT id FROM customer WHERE first_name LIKE @Pattern)", pattern);
            await connection.ExecuteAsync($"DELETE FROM vehicle WHERE id IN ({markerVehicles})", pattern);
            await connection.ExecuteAsync("DELETE FROM vendor WHERE name LIKE @Pattern", pattern);
            await connection.ExecuteAsync("DELETE FROM customer WHERE first_name LIKE @Pattern", pattern);
            await connection.ExecuteAsync("DELETE FROM employee WHERE first_name LIKE @Pattern", pattern);
        }

        private async Task<long> CreateCustomerAsync(Status status = Status.Active)
        {
            var customer = await _customerRepository.AddCustomer(new CustomerCreateDto
            {
                First_Name = _marker,
                Last_Name = "Buyer",
                Email = $"{_marker}@example.com",
                Status = status
            });
            _customerIds.Add(customer.Id);
            return customer.Id;
        }

        private async Task<long> CreateEmployeeAsync(Status status = Status.Active, decimal? commission = 5m)
        {
            var employee = await _employeeRepository.AddEmployee(new EmployeeCreateDto
            {
                First_Name = _marker,
                Last_Name = "Seller",
                Department = "Sales",
                Commission_Rate = commission,
                Status = status
            });
            _employeeIds.Add(employee.Id);
            return employee.Id;
        }

        private async Task<VehicleResponseDto> CreateVehicleAsync(string suffix = "1", string status = "available")
        {
            var vendor = await _vendorRepository.AddVendor(new VendorCreateDto
            {
                Name = $"{_marker} Vendor",
                Type = VendorType.Vehicles,
                Status = Status.Active
            });
            _vendorIds.Add(vendor.Id);

            var vehicle = await _vehicleRepository.AddVehicle(new VehicleCreateDto
            {
                Vendor_Id = vendor.Id,
                Vin = $"{_marker}{suffix}".ToUpperInvariant().PadRight(17, '0').Substring(0, 17),
                Make = "Toyota",
                Model = "Camry",
                Purchase_Price = 20000m,
                Additional_Costs = 500m,
                Selling_Price = 25000m
            });
            _vehicleIds.Add(vehicle.Id);

            if (status != "available")
            {
                using IDbConnection connection = _repositoryContext.GetDatabaseConnection();
                await connection.ExecuteAsync("UPDATE vehicle SET status = @Status WHERE id = @Id",
                    new { Status = status, Id = vehicle.Id });
            }
            return vehicle;
        }

        private static SalesInvoiceCreateDto NewDto(long customerId, long salespersonId, long vehicleId) => new()
        {
            Customer_Id = customerId,
            Salesperson_Id = salespersonId,
            Vehicle_Id = vehicleId,
            Invoice_Date = new DateTime(2026, 5, 1),
            Due_Date = new DateTime(2026, 5, 31),
            Tax = 1500m,
            Fees = 300m,
            Discount = 500m
        };

        private async Task<SalesInvoiceResponseDto> AddAsync(SalesInvoiceCreateDto dto)
        {
            var created = await _repository.AddSalesInvoice(dto);
            _invoiceIds.Add(created.Id);
            return created;
        }

        private async Task<SalesInvoiceCreateDto> ValidDtoAsync(string suffix = "1")
        {
            var customerId = await CreateCustomerAsync();
            var salespersonId = await CreateEmployeeAsync();
            var vehicle = await CreateVehicleAsync(suffix);
            return NewDto(customerId, salespersonId, vehicle.Id);
        }

        [Fact]
        public async Task AddSalesInvoice_ReturnsSavedDraftWithInputValues()
        {
            var dto = await ValidDtoAsync();

            var created = await AddAsync(dto);

            Assert.True(created.Id > 0);
            Assert.Equal(dto.Customer_Id, created.Customer_Id);
            Assert.Equal(dto.Salesperson_Id, created.Salesperson_Id);
            Assert.Equal(dto.Vehicle_Id, created.Vehicle_Id);
            Assert.Equal(new DateTime(2026, 5, 1), created.Invoice_Date);
            Assert.Equal(new DateTime(2026, 5, 31), created.Due_Date);
            Assert.Equal(25000m, created.Sale_Price); // vehicle.selling_price
            Assert.Equal(1500m, created.Tax);
            Assert.Equal(300m, created.Fees);
            Assert.Equal(500m, created.Discount);
        }

        [Fact]
        public async Task AddSalesInvoice_StoresDraftLifecycleTotalAndBalance()
        {
            var created = await AddAsync(await ValidDtoAsync());

            using IDbConnection connection = _repositoryContext.GetDatabaseConnection();
            var row = await connection.QuerySingleAsync<(string Lifecycle, string? PaymentStatus, decimal Total, decimal Paid, decimal Balance)>(
                "SELECT lifecycle, payment_status AS PaymentStatus, total, paid, balance FROM sales_invoice WHERE id = @Id", new { created.Id });

            Assert.Equal(InvoiceLifecycle.Draft.ToDbValue(), row.Lifecycle);
            Assert.Null(row.PaymentStatus);
            Assert.Equal(26300m, row.Total);
            Assert.Equal(0m, row.Paid);
            Assert.Equal(26300m, row.Balance);
        }

        [Fact]
        public async Task AddSalesInvoice_DoesNotChangeVehicle()
        {
            var dto = await ValidDtoAsync();

            await AddAsync(dto);

            var vehicle = await _vehicleRepository.GetVehicleById(dto.Vehicle_Id);
            Assert.NotNull(vehicle);
            Assert.Equal(VehicleStatus.Available, vehicle!.Status);
            using IDbConnection connection = _repositoryContext.GetDatabaseConnection();
            var invoiceId = await connection.ExecuteScalarAsync<long?>(
                "SELECT invoice_id FROM vehicle WHERE id = @Id", new { Id = dto.Vehicle_Id });
            Assert.Null(invoiceId);
        }

        [Theory]
        [InlineData("reserved")]
        [InlineData("sold")]
        public async Task AddSalesInvoice_VehicleNotAvailable_Throws(string vehicleStatus)
        {
            var customerId = await CreateCustomerAsync();
            var salespersonId = await CreateEmployeeAsync();
            var vehicle = await CreateVehicleAsync("1", vehicleStatus);

            var ex = await Assert.ThrowsAsync<ArgumentException>(
                () => _repository.AddSalesInvoice(NewDto(customerId, salespersonId, vehicle.Id)));

            Assert.Equal("Only available vehicles can be selected.", ex.Message);
        }

        [Fact]
        public async Task AddSalesInvoice_InactiveCustomer_Throws()
        {
            var customerId = await CreateCustomerAsync(Status.Inactive);
            var salespersonId = await CreateEmployeeAsync();
            var vehicle = await CreateVehicleAsync();

            var ex = await Assert.ThrowsAsync<ArgumentException>(
                () => _repository.AddSalesInvoice(NewDto(customerId, salespersonId, vehicle.Id)));

            Assert.Equal("Only active customers can be selected.", ex.Message);
        }

        [Fact]
        public async Task AddSalesInvoice_InactiveSalesperson_Throws()
        {
            var customerId = await CreateCustomerAsync();
            var salespersonId = await CreateEmployeeAsync(Status.Inactive);
            var vehicle = await CreateVehicleAsync();

            var ex = await Assert.ThrowsAsync<ArgumentException>(
                () => _repository.AddSalesInvoice(NewDto(customerId, salespersonId, vehicle.Id)));

            Assert.Equal("Only active salespeople can be assigned.", ex.Message);
        }

        [Theory]
        [InlineData(null)]
        [InlineData(0)]
        public async Task AddSalesInvoice_EmployeeWithoutCommission_IsNotSalesperson_Throws(int? commission)
        {
            var customerId = await CreateCustomerAsync();
            var employeeId = await CreateEmployeeAsync(Status.Active, commission);
            var vehicle = await CreateVehicleAsync();

            var ex = await Assert.ThrowsAsync<ArgumentException>(
                () => _repository.AddSalesInvoice(NewDto(customerId, employeeId, vehicle.Id)));

            Assert.Equal("Only active salespeople can be assigned.", ex.Message);
        }

        [Fact]
        public async Task AddSalesInvoice_RejectedInvoice_IsNotSaved()
        {
            var customerId = await CreateCustomerAsync(Status.Inactive);
            var salespersonId = await CreateEmployeeAsync();
            var vehicle = await CreateVehicleAsync();

            await Assert.ThrowsAsync<ArgumentException>(
                () => _repository.AddSalesInvoice(NewDto(customerId, salespersonId, vehicle.Id)));

            using IDbConnection connection = _repositoryContext.GetDatabaseConnection();
            var count = await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM sales_invoice WHERE vehicle_id = @Id", new { Id = vehicle.Id });
            Assert.Equal(0, count);
        }

        [Fact]
        public async Task AddSalesInvoice_VehicleAlreadyHasDraft_AllowsSecondDraft()
        {
            // Story 12 AC5: a vehicle can be on several drafts; only one posted invoice is allowed.
            var dto = await ValidDtoAsync();
            var first = await AddAsync(dto);

            var second = await AddAsync(dto);

            Assert.NotEqual(first.Id, second.Id);
        }

        // ---- AddSalesInvoice(dto, tx): runs inside the caller's transaction (used by CreateAndPost) ----
        // Nothing is visible to other connections until the caller commits.

        private (IDbConnection Connection, IDbTransaction Tx) Begin()
        {
            var connection = _repositoryContext.GetDatabaseConnection();
            if (connection.State != ConnectionState.Open)
                connection.Open();
            return (connection, connection.BeginTransaction());
        }

        private async Task<int> InvoiceCountForVehicleAsync(long vehicleId)
        {
            using IDbConnection connection = _repositoryContext.GetDatabaseConnection();
            return await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM sales_invoice WHERE vehicle_id = @Id", new { Id = vehicleId });
        }

        [Fact]
        public async Task AddSalesInvoiceInTransaction_ReturnsSavedDraftWithInputValues()
        {
            var dto = await ValidDtoAsync();
            var (connection, tx) = Begin();
            using var _ = connection;
            using var __ = tx;

            var created = await _repository.AddSalesInvoice(dto, tx);
            tx.Commit();

            Assert.True(created.Id > 0);
            Assert.Equal(dto.Customer_Id, created.Customer_Id);
            Assert.Equal(dto.Salesperson_Id, created.Salesperson_Id);
            Assert.Equal(dto.Vehicle_Id, created.Vehicle_Id);
            Assert.Equal(new DateTime(2026, 5, 1), created.Invoice_Date);
            Assert.Equal(new DateTime(2026, 5, 31), created.Due_Date);
            Assert.Equal(25000m, created.Sale_Price); // vehicle.selling_price
            Assert.Equal(1500m, created.Tax);
            Assert.Equal(300m, created.Fees);
            Assert.Equal(500m, created.Discount);
            Assert.Equal(InvoiceLifecycle.Draft, created.Lifecycle);
            Assert.Equal($"INV-{created.Id:D6}", created.Number);
        }

        [Fact]
        public async Task AddSalesInvoiceInTransaction_AfterCommit_StoresDraftLifecycleTotalAndBalance()
        {
            var dto = await ValidDtoAsync();
            var (connection, tx) = Begin();
            using var _ = connection;
            using var __ = tx;

            var created = await _repository.AddSalesInvoice(dto, tx);
            tx.Commit();

            using IDbConnection reader = _repositoryContext.GetDatabaseConnection();
            var row = await reader.QuerySingleAsync<(string Lifecycle, string? PaymentStatus, decimal Total, decimal Paid, decimal Balance)>(
                "SELECT lifecycle, payment_status AS PaymentStatus, total, paid, balance FROM sales_invoice WHERE id = @Id", new { created.Id });
            Assert.Equal(InvoiceLifecycle.Draft.ToDbValue(), row.Lifecycle);
            Assert.Null(row.PaymentStatus);
            Assert.Equal(26300m, row.Total);
            Assert.Equal(0m, row.Paid);
            Assert.Equal(26300m, row.Balance);
        }

        [Fact]
        public async Task AddSalesInvoiceInTransaction_BeforeCommit_IsNotVisibleToOtherConnections()
        {
            var dto = await ValidDtoAsync();
            var (connection, tx) = Begin();
            using var _ = connection;
            using var __ = tx;

            var created = await _repository.AddSalesInvoice(dto, tx);

            Assert.Null(await _repository.GetInvoiceById(created.Id));
            Assert.Equal(0, await InvoiceCountForVehicleAsync(dto.Vehicle_Id));
            tx.Commit();
            Assert.NotNull(await _repository.GetInvoiceById(created.Id));
        }

        [Fact]
        public async Task AddSalesInvoiceInTransaction_RolledBack_LeavesNoInvoice()
        {
            var dto = await ValidDtoAsync();
            var (connection, tx) = Begin();
            using var _ = connection;
            using var __ = tx;

            var created = await _repository.AddSalesInvoice(dto, tx);
            tx.Rollback();

            Assert.Null(await _repository.GetInvoiceById(created.Id));
            Assert.Equal(0, await InvoiceCountForVehicleAsync(dto.Vehicle_Id));
        }

        [Fact]
        public async Task AddSalesInvoiceInTransaction_DoesNotChangeVehicle()
        {
            var dto = await ValidDtoAsync();
            var (connection, tx) = Begin();
            using var _ = connection;
            using var __ = tx;

            await _repository.AddSalesInvoice(dto, tx);
            tx.Commit();

            var vehicle = await _vehicleRepository.GetVehicleById(dto.Vehicle_Id);
            Assert.Equal(VehicleStatus.Available, vehicle!.Status);
            using IDbConnection reader = _repositoryContext.GetDatabaseConnection();
            Assert.Null(await reader.ExecuteScalarAsync<long?>(
                "SELECT invoice_id FROM vehicle WHERE id = @Id", new { Id = dto.Vehicle_Id }));
        }

        [Fact]
        public async Task AddSalesInvoiceInTransaction_TwoInOneTransaction_BothAreSavedOnCommit()
        {
            var dto = await ValidDtoAsync();
            var (connection, tx) = Begin();
            using var _ = connection;
            using var __ = tx;

            var first = await _repository.AddSalesInvoice(dto, tx);
            var second = await _repository.AddSalesInvoice(dto, tx);
            tx.Commit();

            Assert.NotEqual(first.Id, second.Id);
            Assert.Equal(2, await InvoiceCountForVehicleAsync(dto.Vehicle_Id));
        }

        [Theory]
        [InlineData("reserved")]
        [InlineData("sold")]
        public async Task AddSalesInvoiceInTransaction_VehicleNotAvailable_Throws(string vehicleStatus)
        {
            var customerId = await CreateCustomerAsync();
            var salespersonId = await CreateEmployeeAsync();
            var vehicle = await CreateVehicleAsync("1", vehicleStatus);
            var (connection, tx) = Begin();
            using var _ = connection;
            using var __ = tx;

            var ex = await Assert.ThrowsAsync<ArgumentException>(
                () => _repository.AddSalesInvoice(NewDto(customerId, salespersonId, vehicle.Id), tx));

            Assert.Equal("Only available vehicles can be selected.", ex.Message);
        }

        [Fact]
        public async Task AddSalesInvoiceInTransaction_InactiveCustomer_Throws()
        {
            var customerId = await CreateCustomerAsync(Status.Inactive);
            var salespersonId = await CreateEmployeeAsync();
            var vehicle = await CreateVehicleAsync();
            var (connection, tx) = Begin();
            using var _ = connection;
            using var __ = tx;

            var ex = await Assert.ThrowsAsync<ArgumentException>(
                () => _repository.AddSalesInvoice(NewDto(customerId, salespersonId, vehicle.Id), tx));

            Assert.Equal("Only active customers can be selected.", ex.Message);
        }

        [Fact]
        public async Task AddSalesInvoiceInTransaction_InactiveSalesperson_Throws()
        {
            var customerId = await CreateCustomerAsync();
            var salespersonId = await CreateEmployeeAsync(Status.Inactive);
            var vehicle = await CreateVehicleAsync();
            var (connection, tx) = Begin();
            using var _ = connection;
            using var __ = tx;

            var ex = await Assert.ThrowsAsync<ArgumentException>(
                () => _repository.AddSalesInvoice(NewDto(customerId, salespersonId, vehicle.Id), tx));

            Assert.Equal("Only active salespeople can be assigned.", ex.Message);
        }

        [Fact]
        public async Task AddSalesInvoiceInTransaction_UnknownCustomer_Throws()
        {
            var salespersonId = await CreateEmployeeAsync();
            var vehicle = await CreateVehicleAsync();
            var (connection, tx) = Begin();
            using var _ = connection;
            using var __ = tx;

            var ex = await Assert.ThrowsAsync<ArgumentException>(
                () => _repository.AddSalesInvoice(NewDto(long.MaxValue, salespersonId, vehicle.Id), tx));

            Assert.Equal("Customer does not exist.", ex.Message);
        }

        [Fact]
        public async Task AddSalesInvoiceInTransaction_RejectedInvoice_IsNotSavedEvenAfterCommit()
        {
            var customerId = await CreateCustomerAsync(Status.Inactive);
            var salespersonId = await CreateEmployeeAsync();
            var vehicle = await CreateVehicleAsync();
            var (connection, tx) = Begin();
            using var _ = connection;
            using var __ = tx;

            await Assert.ThrowsAsync<ArgumentException>(
                () => _repository.AddSalesInvoice(NewDto(customerId, salespersonId, vehicle.Id), tx));
            tx.Commit();

            Assert.Equal(0, await InvoiceCountForVehicleAsync(vehicle.Id));
        }

        // ---- GetInvoiceById(id): stored columns only, for posting a Draft ----

        [Fact]
        public async Task GetInvoiceById_Draft_ReturnsStoredColumns()
        {
            var dto = await ValidDtoAsync();
            var created = await AddAsync(dto);

            var invoice = await _repository.GetInvoiceById(created.Id);

            Assert.NotNull(invoice);
            Assert.Equal(created.Id, invoice!.Id);
            Assert.Equal($"INV-{created.Id:D6}", invoice.Number);
            Assert.Equal(dto.Customer_Id, invoice.CustomerId);
            Assert.Equal(dto.Vehicle_Id, invoice.VehicleId);
            Assert.Equal(new DateTime(2026, 5, 31), invoice.DueDate);
            Assert.Equal(26300m, invoice.Total);
            Assert.Equal(0m, invoice.Paid);
            Assert.Equal(26300m, invoice.Balance);
            Assert.Null(invoice.PaymentStatus);
            Assert.Equal(InvoiceLifecycle.Draft.ToDbValue(), invoice.Lifecycle);
        }

        [Fact]
        public async Task GetInvoiceById_Posted_ReturnsPostedLifecycleAndUnpaidStatus()
        {
            var posted = await PostAsync(await ValidDtoAsync());

            var invoice = await _repository.GetInvoiceById(posted.Id);

            Assert.NotNull(invoice);
            Assert.Equal(InvoiceLifecycle.Posted.ToDbValue(), invoice!.Lifecycle);
            Assert.Equal(PaymentStatus.Unpaid, invoice.PaymentStatus);
            Assert.Equal(0m, invoice.Paid);
            Assert.Equal(26300m, invoice.Balance);
        }

        [Fact]
        public async Task GetInvoiceById_InvoiceWithoutDueDate_ReturnsNullDueDate()
        {
            var dto = await ValidDtoAsync();
            dto.Due_Date = null;
            var created = await AddAsync(dto);

            var invoice = await _repository.GetInvoiceById(created.Id);

            Assert.NotNull(invoice);
            Assert.Null(invoice!.DueDate);
        }

        [Fact]
        public async Task GetInvoiceById_ReturnsOnlyTheRequestedInvoice()
        {
            var first = await AddAsync(await ValidDtoAsync("1"));
            var second = await AddAsync(await ValidDtoAsync("2"));

            var invoice = await _repository.GetInvoiceById(second.Id);

            Assert.Equal(second.Id, invoice!.Id);
            Assert.NotEqual(first.Id, invoice.Id);
        }

        [Fact]
        public async Task GetInvoiceById_Cancelled_ReturnsCancelledLifecycle()
        {
            var posted = await PostAsync(await ValidDtoAsync());
            using (IDbConnection connection = _repositoryContext.GetDatabaseConnection())
                await connection.ExecuteAsync(
                    "UPDATE sales_invoice SET lifecycle = @Cancelled, cancel_reason = 'test' WHERE id = @Id",
                    new { Id = posted.Id, Cancelled = InvoiceLifecycle.Cancelled.ToDbValue() });

            var invoice = await _repository.GetInvoiceById(posted.Id);

            Assert.Equal(InvoiceLifecycle.Cancelled.ToDbValue(), invoice!.Lifecycle);
        }

        [Fact]
        public async Task GetInvoiceById_UnknownId_ReturnsNull()
        {
            var invoice = await _repository.GetInvoiceById(long.MaxValue);

            Assert.Null(invoice);
        }

        // ---- UpdateDraftInvoice: only a Draft can be edited ----

        [Fact]
        public async Task UpdateDraftInvoice_Draft_ReturnsEditedInvoiceWithRecalculatedSalePriceAndStaysDraft()
        {
            var draft = await AddAsync(await ValidDtoAsync());
            var edit = await ValidDtoAsync("2");
            edit.Tax = 2000m;
            edit.Fees = 100m;
            edit.Discount = 300m;
            edit.Due_Date = new DateTime(2026, 6, 30);

            var updated = await _repository.UpdateDraftInvoice(draft.Id, edit);

            Assert.NotNull(updated);
            Assert.Equal(draft.Id, updated!.Id);
            Assert.Equal(draft.Number, updated.Number);
            Assert.Equal(InvoiceLifecycle.Draft, updated.Lifecycle);
            Assert.Equal(edit.Customer_Id, updated.Customer_Id);
            Assert.Equal(edit.Vehicle_Id, updated.Vehicle_Id);
            Assert.Equal(new DateTime(2026, 6, 30), updated.Due_Date);
            Assert.Equal(25000m, updated.Sale_Price); // vehicle.selling_price
            Assert.Equal(2000m, updated.Tax);
            Assert.Equal(100m, updated.Fees);
            Assert.Equal(300m, updated.Discount);
        }

        [Fact]
        public async Task UpdateDraftInvoice_StoresTotalAndBalanceAndKeepsDraftState()
        {
            var draft = await AddAsync(await ValidDtoAsync());
            var edit = await ValidDtoAsync("2");
            edit.Tax = 2000m;
            edit.Fees = 100m;
            edit.Discount = 300m;

            await _repository.UpdateDraftInvoice(draft.Id, edit);

            var invoice = await _repository.GetInvoiceById(draft.Id);
            Assert.Equal(26800m, invoice!.Total); // 25000 + 2000 + 100 - 300
            Assert.Equal(0m, invoice.Paid);
            Assert.Equal(26800m, invoice.Balance);
            Assert.Null(invoice.PaymentStatus);
            Assert.Equal(InvoiceLifecycle.Draft.ToDbValue(), invoice.Lifecycle);
        }

        [Fact]
        public async Task UpdateDraftInvoice_DoesNotChangeEitherVehicle()
        {
            var original = await ValidDtoAsync();
            var draft = await AddAsync(original);
            var edit = await ValidDtoAsync("2");

            await _repository.UpdateDraftInvoice(draft.Id, edit);

            foreach (var vehicleId in new[] { original.Vehicle_Id, edit.Vehicle_Id })
            {
                var vehicle = await _vehicleRepository.GetVehicleById(vehicleId);
                Assert.Equal(VehicleStatus.Available, vehicle!.Status);
            }
        }

        [Fact]
        public async Task UpdateDraftInvoice_UnknownId_ReturnsNull()
        {
            var updated = await _repository.UpdateDraftInvoice(long.MaxValue, await ValidDtoAsync());

            Assert.Null(updated);
        }

        [Fact]
        public async Task UpdateDraftInvoice_PostedInvoice_ThrowsConflictAndChangesNothing()
        {
            var posted = await PostAsync(await ValidDtoAsync());
            var edit = await ValidDtoAsync("2");
            edit.Tax = 9999m;

            var ex = await Assert.ThrowsAsync<ConflictException>(() => _repository.UpdateDraftInvoice(posted.Id, edit));

            Assert.Contains("Draft", ex.Message);
            var invoice = await _repository.GetInvoiceById(posted.Id);
            Assert.Equal(InvoiceLifecycle.Posted.ToDbValue(), invoice!.Lifecycle);
            Assert.Equal(posted.Customer_Id, invoice.CustomerId);
            Assert.Equal(posted.Vehicle_Id, invoice.VehicleId);
        }

        [Fact]
        public async Task UpdateDraftInvoice_InactiveCustomer_ThrowsAndChangesNothing()
        {
            var original = await ValidDtoAsync();
            var draft = await AddAsync(original);
            var edit = await ValidDtoAsync("2");
            edit.Customer_Id = await CreateCustomerAsync(Status.Inactive);

            var ex = await Assert.ThrowsAsync<ArgumentException>(() => _repository.UpdateDraftInvoice(draft.Id, edit));

            Assert.Equal("Only active customers can be selected.", ex.Message);
            var invoice = await _repository.GetInvoiceById(draft.Id);
            Assert.Equal(original.Customer_Id, invoice!.CustomerId);
            Assert.Equal(original.Vehicle_Id, invoice.VehicleId);
        }

        [Fact]
        public async Task UpdateDraftInvoice_VehicleNotAvailable_ThrowsAndChangesNothing()
        {
            var original = await ValidDtoAsync();
            var draft = await AddAsync(original);
            var edit = await ValidDtoAsync("2");
            using (IDbConnection connection = _repositoryContext.GetDatabaseConnection())
                await connection.ExecuteAsync("UPDATE vehicle SET status = 'sold' WHERE id = @Id", new { Id = edit.Vehicle_Id });

            var ex = await Assert.ThrowsAsync<ArgumentException>(() => _repository.UpdateDraftInvoice(draft.Id, edit));

            Assert.Equal("Only available vehicles can be selected.", ex.Message);
            Assert.Equal(original.Vehicle_Id, (await _repository.GetInvoiceById(draft.Id))!.VehicleId);
        }

        [Fact]
        public async Task UpdateDraftInvoice_SameVehicleAsBefore_IsAllowed()
        {
            var original = await ValidDtoAsync();
            var draft = await AddAsync(original);
            original.Tax = 10m;

            var updated = await _repository.UpdateDraftInvoice(draft.Id, original);

            Assert.Equal(original.Vehicle_Id, updated!.Vehicle_Id);
            Assert.Equal(10m, updated.Tax);
        }

        // ---- PostSalesInvoice ----

        private async Task<SalesInvoiceResponseDto> PostAsync(SalesInvoiceCreateDto dto)
        {
            var posted = await _repository.PostSalesInvoice(dto);
            _invoiceIds.Add(posted.Id);
            return posted;
        }

        [Fact]
        public async Task PostSalesInvoice_Valid_ReturnsPostedUnpaidInvoice()
        {
            var dto = await ValidDtoAsync();

            var posted = await PostAsync(dto);

            Assert.True(posted.Id > 0);
            Assert.Equal(InvoiceLifecycle.Posted, posted.Lifecycle);
            Assert.Equal(PaymentStatus.Unpaid, posted.Payment_Status);
            Assert.Equal(0m, posted.Paid);
            Assert.Equal(dto.Vehicle_Id, posted.Vehicle_Id);
            Assert.Equal($"INV-{posted.Id:D6}", posted.Number);
        }

        [Fact]
        public async Task PostSalesInvoice_StoresPostedLifecycleUnpaidTotalAndBalance()
        {
            var posted = await PostAsync(await ValidDtoAsync());

            using IDbConnection connection = _repositoryContext.GetDatabaseConnection();
            var row = await connection.QuerySingleAsync<(string Lifecycle, string PaymentStatus, decimal SalePrice, decimal Total, decimal Paid, decimal Balance)>(
                "SELECT lifecycle, payment_status AS PaymentStatus, sale_price AS SalePrice, total, paid, balance FROM sales_invoice WHERE id = @Id", new { posted.Id });

            Assert.Equal("posted", row.Lifecycle);
            Assert.Equal("unpaid", row.PaymentStatus);
            Assert.Equal(25000m, row.SalePrice);
            Assert.Equal(26300m, row.Total);
            Assert.Equal(0m, row.Paid);
            Assert.Equal(26300m, row.Balance);
        }

        [Fact]
        public async Task PostSalesInvoice_MarksVehicleSoldAndStoresInvoiceId()
        {
            var dto = await ValidDtoAsync();

            var posted = await PostAsync(dto);

            using IDbConnection connection = _repositoryContext.GetDatabaseConnection();
            var row = await connection.QuerySingleAsync<(string Status, long? InvoiceId)>(
                "SELECT status, invoice_id AS InvoiceId FROM vehicle WHERE id = @Id", new { Id = dto.Vehicle_Id });
            Assert.Equal("sold", row.Status);
            Assert.Equal(posted.Id, row.InvoiceId);
        }

        [Fact]
        public async Task PostSalesInvoice_ConcurrentPostsOfSameVehicle_OnlyOneSucceeds()
        {
            var dto = await ValidDtoAsync();

            async Task<bool> Attempt()
            {
                try { await _repository.PostSalesInvoice(dto); return true; }
                catch (ArgumentException) { return false; }
            }

            var results = await Task.WhenAll(Attempt(), Attempt(), Attempt());

            Assert.Single(results, r => r);
            using IDbConnection connection = _repositoryContext.GetDatabaseConnection();
            var ids = (await connection.QueryAsync<long>(
                "SELECT id FROM sales_invoice WHERE vehicle_id = @Id", new { Id = dto.Vehicle_Id })).ToList();
            _invoiceIds.AddRange(ids);
            Assert.Single(ids);
        }

        [Fact]
        public async Task PostSalesInvoice_VehicleAlreadyHasDraft_PostsAndMarksVehicleSold()
        {
            var dto = await ValidDtoAsync();
            await AddAsync(dto);

            var posted = await PostAsync(dto);

            Assert.Equal(InvoiceLifecycle.Posted, posted.Lifecycle);
            var vehicle = await _vehicleRepository.GetVehicleById(dto.Vehicle_Id);
            Assert.Equal(VehicleStatus.Sold, vehicle!.Status);
        }

        [Fact]
        public async Task PostSalesInvoice_VehicleAlreadyPosted_Throws()
        {
            var dto = await ValidDtoAsync();
            await PostAsync(dto);

            await Assert.ThrowsAsync<ArgumentException>(() => _repository.PostSalesInvoice(dto));
        }

        [Theory]
        [InlineData("reserved")]
        [InlineData("sold")]
        public async Task PostSalesInvoice_VehicleNotAvailable_Throws(string vehicleStatus)
        {
            var customerId = await CreateCustomerAsync();
            var salespersonId = await CreateEmployeeAsync();
            var vehicle = await CreateVehicleAsync("1", vehicleStatus);

            var ex = await Assert.ThrowsAsync<ArgumentException>(
                () => _repository.PostSalesInvoice(NewDto(customerId, salespersonId, vehicle.Id)));

            Assert.Equal("Only available vehicles can be selected.", ex.Message);
        }

        [Fact]
        public async Task PostSalesInvoice_InactiveCustomer_Throws()
        {
            var customerId = await CreateCustomerAsync(Status.Inactive);
            var salespersonId = await CreateEmployeeAsync();
            var vehicle = await CreateVehicleAsync();

            var ex = await Assert.ThrowsAsync<ArgumentException>(
                () => _repository.PostSalesInvoice(NewDto(customerId, salespersonId, vehicle.Id)));

            Assert.Equal("Only active customers can be selected.", ex.Message);
        }

        [Fact]
        public async Task PostSalesInvoice_InactiveSalesperson_Throws()
        {
            var customerId = await CreateCustomerAsync();
            var salespersonId = await CreateEmployeeAsync(Status.Inactive);
            var vehicle = await CreateVehicleAsync();

            var ex = await Assert.ThrowsAsync<ArgumentException>(
                () => _repository.PostSalesInvoice(NewDto(customerId, salespersonId, vehicle.Id)));

            Assert.Equal("Only active salespeople can be assigned.", ex.Message);
        }

        [Fact]
        public async Task PostSalesInvoice_DiscountExceedsVehicleSellingPrice_Throws()
        {
            var dto = await ValidDtoAsync();
            dto.Discount = 25000.01m;

            var ex = await Assert.ThrowsAsync<ArgumentException>(() => _repository.PostSalesInvoice(dto));

            Assert.Equal("Discount cannot exceed the sale price.", ex.Message);
        }

        [Fact]
        public async Task PostSalesInvoice_TotalZero_Throws()
        {
            var dto = await ValidDtoAsync();
            dto.Tax = 0m;
            dto.Fees = 0m;
            dto.Discount = 25000m;

            var ex = await Assert.ThrowsAsync<ArgumentException>(() => _repository.PostSalesInvoice(dto));

            Assert.Equal("Invoice total must be greater than zero.", ex.Message);
        }

        [Fact]
        public async Task PostSalesInvoice_VehicleWithoutSellingPrice_Throws()
        {
            var dto = await ValidDtoAsync();
            using (IDbConnection connection = _repositoryContext.GetDatabaseConnection())
                await connection.ExecuteAsync("UPDATE vehicle SET selling_price = NULL WHERE id = @Id", new { Id = dto.Vehicle_Id });

            var ex = await Assert.ThrowsAsync<ArgumentException>(() => _repository.PostSalesInvoice(dto));

            Assert.Equal("The vehicle has no selling price.", ex.Message);
        }

        [Fact]
        public async Task PostSalesInvoice_Rejected_SavesNothingAndKeepsVehicleAvailable()
        {
            var customerId = await CreateCustomerAsync(Status.Inactive);
            var salespersonId = await CreateEmployeeAsync();
            var vehicle = await CreateVehicleAsync();

            await Assert.ThrowsAsync<ArgumentException>(
                () => _repository.PostSalesInvoice(NewDto(customerId, salespersonId, vehicle.Id)));

            using IDbConnection connection = _repositoryContext.GetDatabaseConnection();
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM sales_invoice WHERE vehicle_id = @Id", new { Id = vehicle.Id }));
            var row = await connection.QuerySingleAsync<(string Status, long? InvoiceId)>(
                "SELECT status, invoice_id AS InvoiceId FROM vehicle WHERE id = @Id", new { Id = vehicle.Id });
            Assert.Equal("available", row.Status);
            Assert.Null(row.InvoiceId);
        }

        // ---- Story 9: GetAllSalesInvoices / GetSalesInvoiceById / CancelSalesInvoice ----
        // Every customer created by these tests has First_Name = _marker, so SearchTerm = _marker isolates this test's invoices.

        private SalesInvoiceParameters Params(string? lifecycle = null, string? status = null, string? search = null,
            DateTime? from = null, DateTime? to = null, int pageNumber = 1, int pageSize = 50) => new()
        {
            SearchTerm = search ?? _marker,
            Lifecycle = lifecycle,
            Status = status,
            FromDate = from,
            ToDate = to,
            PageNumber = pageNumber,
            PageSize = pageSize
        };

        private async Task<SalesInvoiceResponseDto> NewPostedAsync(string suffix = "1", DateTime? date = null)
        {
            var dto = await ValidDtoAsync(suffix);
            if (date is not null) dto.Invoice_Date = date.Value;
            return await PostAsync(dto);
        }

        private async Task<SalesInvoiceResponseDto> NewDraftAsync(string suffix = "1")
        {
            return await AddAsync(await ValidDtoAsync(suffix));
        }

        private async Task<SalesInvoiceResponseDto> CancelAsync(long id, string reason = "Entered by mistake")
        {
            return await _repository.CancelCommittedAsync(id, new SalesInvoiceCancelDto { Reason = reason });
        }

        [Fact]
        public async Task GetAllSalesInvoices_NoFilters_ReturnsAllMatchingTheSearch()
        {
            var draft = await NewDraftAsync("1");
            var posted = await NewPostedAsync("2");

            var result = await _repository.GetAllSalesInvoices(Params());

            Assert.Equal(new[] { draft.Id, posted.Id }.OrderBy(x => x), result.Select(i => i.Id).OrderBy(x => x));
            Assert.Equal(2, result.MetaData.TotalCount);
        }

        [Fact]
        public async Task GetAllSalesInvoices_LifecycleFilter_ReturnsOnlyThatLifecycle()
        {
            var draft = await NewDraftAsync("1");
            var posted = await NewPostedAsync("2");

            var drafts = await _repository.GetAllSalesInvoices(Params(lifecycle: "draft"));
            var posteds = await _repository.GetAllSalesInvoices(Params(lifecycle: "Posted"));

            Assert.Equal(draft.Id, Assert.Single(drafts).Id);
            Assert.Equal(posted.Id, Assert.Single(posteds).Id);
        }

        [Fact]
        public async Task GetAllSalesInvoices_CancelledInvoicesAreIncluded()
        {
            var posted = await NewPostedAsync("1");
            await CancelAsync(posted.Id);

            var all = await _repository.GetAllSalesInvoices(Params());
            var cancelled = await _repository.GetAllSalesInvoices(Params(lifecycle: "cancelled"));

            var item = Assert.Single(all);
            Assert.Equal(posted.Id, item.Id);
            Assert.Equal(InvoiceLifecycle.Cancelled, item.Lifecycle);
            Assert.Equal(posted.Id, Assert.Single(cancelled).Id);
        }

        [Fact]
        public async Task GetAllSalesInvoices_StatusFilter_ReturnsOnlyThatPaymentStatus()
        {
            await NewDraftAsync("1");               // draft: no payment status
            var posted = await NewPostedAsync("2");  // posted: unpaid

            var unpaid = await _repository.GetAllSalesInvoices(Params(status: "UNPAID"));
            var paid = await _repository.GetAllSalesInvoices(Params(status: "paid"));

            Assert.Equal(posted.Id, Assert.Single(unpaid).Id);
            Assert.Empty(paid);
        }

        [Fact]
        public async Task GetAllSalesInvoices_LifecycleAndStatus_BothApply()
        {
            var posted = await NewPostedAsync("1");
            await NewDraftAsync("2");

            var both = await _repository.GetAllSalesInvoices(Params(lifecycle: "posted", status: "unpaid"));
            var none = await _repository.GetAllSalesInvoices(Params(lifecycle: "draft", status: "unpaid"));

            Assert.Equal(posted.Id, Assert.Single(both).Id);
            Assert.Empty(none);
        }

        [Fact]
        public async Task GetAllSalesInvoices_SearchByNumber_IsPartialAndCaseInsensitive()
        {
            var posted = await NewPostedAsync("1");
            await NewPostedAsync("2");

            // Number looks like INV-000123; search the lower-case form of the whole number, scoped by nothing else.
            var result = await _repository.GetAllSalesInvoices(Params(search: posted.Number.ToLowerInvariant()));

            Assert.Equal(posted.Id, Assert.Single(result).Id);
        }

        [Fact]
        public async Task GetAllSalesInvoices_SearchByCustomerName_IsPartialAndCaseInsensitive()
        {
            var posted = await NewPostedAsync("1");

            var result = await _repository.GetAllSalesInvoices(Params(search: $"{_marker.ToUpperInvariant()} BUY"));

            Assert.Equal(posted.Id, Assert.Single(result).Id);
        }

        [Fact]
        public async Task GetAllSalesInvoices_SearchByVin_IsPartialAndCaseInsensitive()
        {
            var posted = await NewPostedAsync("1");
            var vin = (await _vehicleRepository.GetVehicleById(posted.Vehicle_Id))!.Vin;

            var result = await _repository.GetAllSalesInvoices(Params(search: vin.Substring(2, 12).ToLowerInvariant()));

            Assert.Equal(posted.Id, Assert.Single(result).Id);
        }

        [Fact]
        public async Task GetAllSalesInvoices_SearchAndLifecycle_BothApply()
        {
            await NewDraftAsync("1");
            var posted = await NewPostedAsync("2");

            var result = await _repository.GetAllSalesInvoices(Params(lifecycle: "posted", search: _marker));

            Assert.Equal(posted.Id, Assert.Single(result).Id);
        }

        [Fact]
        public async Task GetAllSalesInvoices_DateRange_FromAndToAreInclusive()
        {
            var may = await NewPostedAsync("1", new DateTime(2026, 5, 1));
            var june = await NewPostedAsync("2", new DateTime(2026, 6, 1));
            var july = await NewPostedAsync("3", new DateTime(2026, 7, 1));

            var from = await _repository.GetAllSalesInvoices(Params(from: new DateTime(2026, 6, 1)));
            var to = await _repository.GetAllSalesInvoices(Params(to: new DateTime(2026, 6, 1)));
            var between = await _repository.GetAllSalesInvoices(Params(from: new DateTime(2026, 6, 1), to: new DateTime(2026, 6, 1)));

            Assert.Equal(new[] { june.Id, july.Id }.OrderBy(x => x), from.Select(i => i.Id).OrderBy(x => x));
            Assert.Equal(new[] { may.Id, june.Id }.OrderBy(x => x), to.Select(i => i.Id).OrderBy(x => x));
            Assert.Equal(june.Id, Assert.Single(between).Id);
        }

        [Fact]
        public async Task GetAllSalesInvoices_OrderedByDateDescendingThenIdDescending()
        {
            var may = await NewPostedAsync("1", new DateTime(2026, 5, 1));
            var juneFirst = await NewPostedAsync("2", new DateTime(2026, 6, 1));
            var juneSecond = await NewPostedAsync("3", new DateTime(2026, 6, 1));

            var result = await _repository.GetAllSalesInvoices(Params());

            Assert.Equal(new[] { juneSecond.Id, juneFirst.Id, may.Id }, result.Select(i => i.Id));
        }

        [Fact]
        public async Task GetAllSalesInvoices_Paging_SecondPageAndTotalCountCountsAllMatches()
        {
            var first = await NewPostedAsync("1", new DateTime(2026, 5, 1));
            var second = await NewPostedAsync("2", new DateTime(2026, 6, 1));
            var third = await NewPostedAsync("3", new DateTime(2026, 7, 1));

            var page2 = await _repository.GetAllSalesInvoices(Params(pageNumber: 2, pageSize: 2));

            // Newest first: [third, second] | [first]
            Assert.Equal(first.Id, Assert.Single(page2).Id);
            Assert.Equal(3, page2.MetaData.TotalCount);
            Assert.Equal(2, page2.MetaData.CurrentPage);
            Assert.Equal(2, page2.MetaData.PageSize);
            Assert.Equal(2, page2.MetaData.TotalPages);
        }

        [Fact]
        public async Task GetAllSalesInvoices_PageSizeAboveCap_IsClamped()
        {
            await NewPostedAsync("1");

            var result = await _repository.GetAllSalesInvoices(Params(pageSize: 500));

            Assert.Equal(100, result.MetaData.PageSize);
        }

        [Fact]
        public async Task GetAllSalesInvoices_NoMatches_ReturnsEmptyPagedList()
        {
            await NewPostedAsync("1");

            var result = await _repository.GetAllSalesInvoices(Params(search: $"{_marker}-no-such-invoice"));

            Assert.NotNull(result);
            Assert.Empty(result);
            Assert.Equal(0, result.MetaData.TotalCount);
        }

        [Fact]
        public async Task GetAllSalesInvoices_IncludesJoinedCustomerAndVehicleColumns()
        {
            var posted = await NewPostedAsync("1");

            var item = Assert.Single(await _repository.GetAllSalesInvoices(Params()));

            Assert.Equal($"{_marker} Buyer", item.Customer_Name);
            Assert.Equal(posted.Vehicle_Id, item.Vehicle_Id);
            Assert.Equal("Toyota", item.Make);
            Assert.Equal("Camry", item.Model);
            Assert.Equal(17, item.Vin.Length);
            Assert.Equal("sold", item.Vehicle_Status);
            Assert.Equal(25000m, item.Sale_Price);
            Assert.Equal(PaymentStatus.Unpaid, item.Payment_Status);
        }

        [Fact]
        public async Task GetAllSalesInvoices_CalculatedFieldsAreNotFilled()
        {
            await NewPostedAsync("1");

            var item = Assert.Single(await _repository.GetAllSalesInvoices(Params()));

            // Total and Balance are the service's job.
            Assert.Equal(0m, item.Total);
            Assert.Equal(0m, item.Balance);
        }

        [Fact]
        public async Task GetSalesInvoiceById_Existing_ReturnsInvoiceWithJoinedColumns()
        {
            var posted = await NewPostedAsync("1");

            var found = await _repository.GetSalesInvoiceById(posted.Id);

            Assert.NotNull(found);
            Assert.Equal(posted.Id, found!.Id);
            Assert.Equal(posted.Number, found.Number);
            Assert.Equal($"{_marker} Buyer", found.Customer_Name);
            Assert.Equal("Toyota", found.Make);
            Assert.Equal(InvoiceLifecycle.Posted, found.Lifecycle);
            Assert.Null(found.Cancel_Reason);
        }

        [Fact]
        public async Task GetSalesInvoiceById_Unknown_ReturnsNull()
        {
            Assert.Null(await _repository.GetSalesInvoiceById(long.MaxValue));
        }

        [Fact]
        public async Task CancelSalesInvoice_Posted_ReturnsCancelledInvoiceWithReason()
        {
            var posted = await NewPostedAsync("1");

            var cancelled = await CancelAsync(posted.Id, "Customer changed their mind");

            Assert.Equal(posted.Id, cancelled.Id);
            Assert.Equal(InvoiceLifecycle.Cancelled, cancelled.Lifecycle);
            Assert.Equal("Customer changed their mind", cancelled.Cancel_Reason);
        }

        [Fact]
        public async Task CancelSalesInvoice_StoresLifecycleReasonAndKeepsPaymentStatusAndVehicle()
        {
            var posted = await NewPostedAsync("1");

            await CancelAsync(posted.Id, "Wrong vehicle");

            using IDbConnection connection = _repositoryContext.GetDatabaseConnection();
            var row = await connection.QuerySingleAsync<(string Lifecycle, string CancelReason, string PaymentStatus, long VehicleId)>(
                "SELECT lifecycle, cancel_reason AS CancelReason, payment_status AS PaymentStatus, vehicle_id AS VehicleId FROM sales_invoice WHERE id = @Id",
                new { posted.Id });
            Assert.Equal("cancelled", row.Lifecycle);
            Assert.Equal("Wrong vehicle", row.CancelReason);
            Assert.Equal("unpaid", row.PaymentStatus);
            Assert.Equal(posted.Vehicle_Id, row.VehicleId);
        }

        [Fact]
        public async Task CancelSalesInvoice_ReturnsVehicleToInventory()
        {
            var posted = await NewPostedAsync("1");

            await CancelAsync(posted.Id);

            using IDbConnection connection = _repositoryContext.GetDatabaseConnection();
            var row = await connection.QuerySingleAsync<(string Status, long? InvoiceId)>(
                "SELECT status, invoice_id AS InvoiceId FROM vehicle WHERE id = @Id", new { Id = posted.Vehicle_Id });
            Assert.Equal("available", row.Status);
            Assert.Null(row.InvoiceId);
        }

        [Fact]
        public async Task CancelSalesInvoice_InvoiceStaysReadableAfterwards()
        {
            var posted = await NewPostedAsync("1");
            await CancelAsync(posted.Id);

            var found = await _repository.GetSalesInvoiceById(posted.Id);

            Assert.NotNull(found);
            Assert.Equal(InvoiceLifecycle.Cancelled, found!.Lifecycle);
            Assert.Equal(posted.Number, found.Number);
        }

        // Needs uq_sales_invoice_vehicle_active (unique vehicle_id WHERE lifecycle <> 'cancelled') instead of a plain unique constraint.
        [Fact]
        public async Task CancelSalesInvoice_VehicleCanBeInvoicedAgain_AndOldInvoiceKeepsItsVehicle()
        {
            var dto = await ValidDtoAsync();
            var first = await PostAsync(dto);
            await CancelAsync(first.Id);

            var second = await PostAsync(dto);

            Assert.NotEqual(first.Id, second.Id);
            Assert.Equal(InvoiceLifecycle.Posted, second.Lifecycle);
            var old = await _repository.GetSalesInvoiceById(first.Id);
            Assert.Equal(dto.Vehicle_Id, old!.Vehicle_Id);
            Assert.Equal(InvoiceLifecycle.Cancelled, old.Lifecycle);
            var vehicle = await _vehicleRepository.GetVehicleById(dto.Vehicle_Id);
            Assert.Equal(VehicleStatus.Sold, vehicle!.Status);
        }

        [Fact]
        public async Task CancelSalesInvoice_UnknownId_ThrowsKeyNotFound()
        {
            await Assert.ThrowsAsync<KeyNotFoundException>(() => CancelAsync(long.MaxValue));
        }

        [Fact]
        public async Task CancelSalesInvoice_Draft_Throws_AndDraftIsUnchanged()
        {
            var draft = await NewDraftAsync("1");

            var ex = await Assert.ThrowsAsync<ArgumentException>(() => CancelAsync(draft.Id));

            Assert.Equal("Only Posted invoices can be cancelled.", ex.Message);
            var found = await _repository.GetSalesInvoiceById(draft.Id);
            Assert.Equal(InvoiceLifecycle.Draft, found!.Lifecycle);
        }

        [Fact]
        public async Task CancelSalesInvoice_AlreadyCancelled_Throws()
        {
            var posted = await NewPostedAsync("1");
            await CancelAsync(posted.Id, "First reason");

            var ex = await Assert.ThrowsAsync<ArgumentException>(() => CancelAsync(posted.Id, "Second reason"));

            Assert.Equal("Only Posted invoices can be cancelled.", ex.Message);
            var found = await _repository.GetSalesInvoiceById(posted.Id);
            Assert.Equal("First reason", found!.Cancel_Reason);
        }

        [Fact]
        public async Task CancelSalesInvoice_WithPayments_Throws_AndNothingChanges()
        {
            var posted = await NewPostedAsync("1");
            using (IDbConnection connection = _repositoryContext.GetDatabaseConnection())
                await connection.ExecuteAsync("UPDATE sales_invoice SET paid = 100 WHERE id = @Id", new { posted.Id });

            var ex = await Assert.ThrowsAsync<ArgumentException>(() => CancelAsync(posted.Id));

            Assert.Equal("An invoice with payments cannot be cancelled.", ex.Message);
            var found = await _repository.GetSalesInvoiceById(posted.Id);
            Assert.Equal(InvoiceLifecycle.Posted, found!.Lifecycle);
            Assert.Null(found.Cancel_Reason);
            var vehicle = await _vehicleRepository.GetVehicleById(posted.Vehicle_Id);
            Assert.Equal(VehicleStatus.Sold, vehicle!.Status);
        }

        // ---- GetSalesInvoiceByCustomer (Story 14) ----
        // Customer Alpha (A) invoices (all 2020 Honda Civic, selling price 18000, created in this order):
        //   X1  posted     2026-03-01  tax 500 -> total 18500  paid 0      unpaid
        //   X2  posted     2026-05-01  total 18000             paid 4000   partial
        //   X3  cancelled  2026-04-01  total 18000             paid 0      unpaid
        //   X4  draft      2026-06-01  total 18000             paid 0      (no payment status)   -> never listed
        //   X5  posted     2026-05-01  total 18000             paid 18000  paid  (same date as X2, higher number)
        // Customer Beta (B): no invoices.  Customer Gamma (G): G1 posted 2026-02-01, 2019 Toyota Corolla, total 20000.
        // Expected order for Alpha: X2, X5, X3, X1.
        private int _byCustomerVehicleCount;
        private long _byCustomerVendorId;

        private sealed record ByCustomerSeed(long AlphaId, long BetaId, long GammaId, Dictionary<string, string> Numbers)
        {
            public string Number(string key) => Numbers[key];

            public string[] Keys(IEnumerable<SalesInvoiceByCustomerDto> items) =>
                items.Select(i => Numbers.First(n => n.Value == i.Invoice_Number).Key).ToArray();
        }

        private async Task<ByCustomerSeed> SeedByCustomerAsync()
        {
            var alphaId = await CreateCustomerAsync();
            var betaId = await CreateCustomerAsync();
            var gammaId = await CreateCustomerAsync();
            var salespersonId = await CreateEmployeeAsync();
            var numbers = new Dictionary<string, string>();

            await AddByCustomerInvoiceAsync(numbers, "X1", alphaId, salespersonId, new DateTime(2026, 3, 1), "posted", "unpaid", 0m, tax: 500m);
            await AddByCustomerInvoiceAsync(numbers, "X2", alphaId, salespersonId, new DateTime(2026, 5, 1), "posted", "partial", 4000m);
            await AddByCustomerInvoiceAsync(numbers, "X3", alphaId, salespersonId, new DateTime(2026, 4, 1), "cancelled", "unpaid", 0m);
            await AddByCustomerInvoiceAsync(numbers, "X4", alphaId, salespersonId, new DateTime(2026, 6, 1), "draft", null, 0m);
            await AddByCustomerInvoiceAsync(numbers, "X5", alphaId, salespersonId, new DateTime(2026, 5, 1), "posted", "paid", 18000m);
            await AddByCustomerInvoiceAsync(numbers, "G1", gammaId, salespersonId, new DateTime(2026, 2, 1), "posted", "unpaid", 0m,
                year: 2019, make: "Toyota", model: "Corolla", sellingPrice: 20000m);
            return new ByCustomerSeed(alphaId, betaId, gammaId, numbers);
        }

        // A draft invoice is created through the repository (for the FKs and the stored total), then its
        // lifecycle, payment status and paid amount are set straight in the table.
        private async Task AddByCustomerInvoiceAsync(Dictionary<string, string> numbers, string key, long customerId,
            long salespersonId, DateTime date, string lifecycle, string? paymentStatus, decimal paid, decimal tax = 0m,
            int year = 2020, string make = "Honda", string model = "Civic", decimal sellingPrice = 18000m)
        {
            if (_byCustomerVendorId == 0)
            {
                var vendor = await new VendorRepository(_repositoryContext).AddVendor(new VendorCreateDto
                {
                    Name = $"{_marker} Vendor", Type = VendorType.Vehicles, Status = Status.Active
                });
                _byCustomerVendorId = vendor.Id;
            }
            var vehicle = await _vehicleRepository.AddVehicle(new VehicleCreateDto
            {
                Vendor_Id = _byCustomerVendorId,
                Vin = $"{_marker}B{++_byCustomerVehicleCount}".ToUpperInvariant().PadRight(17, '0').Substring(0, 17),
                Make = make, Model = model, Year = year,
                Purchase_Price = 15000m, Additional_Costs = 250m, Selling_Price = sellingPrice
            });
            var invoice = await _repository.AddSalesInvoice(new SalesInvoiceCreateDto
            {
                Customer_Id = customerId, Salesperson_Id = salespersonId, Vehicle_Id = vehicle.Id,
                Invoice_Date = date, Tax = tax
            });
            numbers[key] = invoice.Number;

            using var connection = _repositoryContext.GetDatabaseConnection();
            await connection.ExecuteAsync(
                @"UPDATE sales_invoice
                  SET lifecycle = @Lifecycle, payment_status = @PaymentStatus, paid = @Paid, cancel_reason = @CancelReason
                  WHERE id = @Id",
                new
                {
                    invoice.Id, Lifecycle = lifecycle, PaymentStatus = paymentStatus, Paid = paid,
                    CancelReason = lifecycle == "cancelled" ? "test" : null
                });
        }

        // ---- Lifecycle: posted and cancelled only ----

        [Fact]
        public async Task GetSalesInvoiceByCustomer_DraftInvoiceIsLeftOut()
        {
            var s = await SeedByCustomerAsync();
            var items = await _repository.GetSalesInvoiceByCustomer(s.AlphaId);

            Assert.DoesNotContain(items, i => i.Invoice_Number == s.Number("X4"));
        }

        [Fact]
        public async Task GetSalesInvoiceByCustomer_CancelledInvoiceIsListed()
        {
            var s = await SeedByCustomerAsync();
            var items = await _repository.GetSalesInvoiceByCustomer(s.AlphaId);

            Assert.Contains(items, i => i.Invoice_Number == s.Number("X3"));
        }

        [Fact]
        public async Task GetSalesInvoiceByCustomer_ListsPostedAndCancelledOnly()
        {
            var s = await SeedByCustomerAsync();
            var items = await _repository.GetSalesInvoiceByCustomer(s.AlphaId);

            Assert.Equal(4, items.Count);
        }

        [Fact]
        public async Task GetSalesInvoiceByCustomer_EveryItemHasAPaymentStatus()
        {
            var s = await SeedByCustomerAsync();
            var items = await _repository.GetSalesInvoiceByCustomer(s.AlphaId);

            Assert.All(items, i => Assert.NotNull(i.Payment_Status));
        }

        // ---- Customer filter ----

        [Fact]
        public async Task GetSalesInvoiceByCustomer_ReturnsOnlyThatCustomersInvoices()
        {
            var s = await SeedByCustomerAsync();
            var items = await _repository.GetSalesInvoiceByCustomer(s.GammaId);

            Assert.Equal(new[] { "G1" }, s.Keys(items));
        }

        [Fact]
        public async Task GetSalesInvoiceByCustomer_CustomerWithoutInvoices_ReturnsEmptyList()
        {
            var s = await SeedByCustomerAsync();
            var items = await _repository.GetSalesInvoiceByCustomer(s.BetaId);

            Assert.NotNull(items);
            Assert.Empty(items);
        }

        [Fact]
        public async Task GetSalesInvoiceByCustomer_UnknownCustomer_ReturnsEmptyList()
        {
            var items = await _repository.GetSalesInvoiceByCustomer(long.MaxValue);

            Assert.NotNull(items);
            Assert.Empty(items);
        }

        // ---- Order ----

        [Fact]
        public async Task GetSalesInvoiceByCustomer_OrderedByDateNewestFirstThenNumber()
        {
            var s = await SeedByCustomerAsync();
            var items = await _repository.GetSalesInvoiceByCustomer(s.AlphaId);

            Assert.Equal(new[] { "X2", "X5", "X3", "X1" }, s.Keys(items));
        }

        // ---- Columns ----

        [Fact]
        public async Task GetSalesInvoiceByCustomer_FillsEveryColumn()
        {
            var s = await SeedByCustomerAsync();
            var item = (await _repository.GetSalesInvoiceByCustomer(s.AlphaId)).Single(i => i.Invoice_Number == s.Number("X2"));

            Assert.Equal(new DateTime(2026, 5, 1), item.Invoice_Date);
            Assert.Equal("2020 Honda Civic", item.Vehicle);
            Assert.Equal($"{_marker} Seller", item.Salesperson);
            Assert.Equal(18000m, item.Amount);
            Assert.Equal(4000m, item.Paid);
            Assert.Equal("partial", item.Payment_Status);
        }

        [Fact]
        public async Task GetSalesInvoiceByCustomer_AmountIsTheStoredTotalIncludingTax()
        {
            var s = await SeedByCustomerAsync();
            var item = (await _repository.GetSalesInvoiceByCustomer(s.AlphaId)).Single(i => i.Invoice_Number == s.Number("X1"));

            Assert.Equal(18500m, item.Amount);
        }

        [Fact]
        public async Task GetSalesInvoiceByCustomer_VehicleIsYearMakeModelOfThatInvoice()
        {
            var s = await SeedByCustomerAsync();
            var item = (await _repository.GetSalesInvoiceByCustomer(s.GammaId)).Single();

            Assert.Equal("2019 Toyota Corolla", item.Vehicle);
            Assert.Equal(20000m, item.Amount);
        }

        [Theory]
        [InlineData("X1", "unpaid")]
        [InlineData("X2", "partial")]
        [InlineData("X5", "paid")]
        public async Task GetSalesInvoiceByCustomer_PaymentStatusIsTheStoredValue(string key, string expected)
        {
            var s = await SeedByCustomerAsync();
            var item = (await _repository.GetSalesInvoiceByCustomer(s.AlphaId)).Single(i => i.Invoice_Number == s.Number(key));

            Assert.Equal(expected, item.Payment_Status);
        }

        // ---- Read-only ----

        [Fact]
        public async Task GetSalesInvoiceByCustomer_DoesNotChangeTheStoredRows()
        {
            var s = await SeedByCustomerAsync();
            await _repository.GetSalesInvoiceByCustomer(s.AlphaId);

            using IDbConnection connection = _repositoryContext.GetDatabaseConnection();
            var rows = (await connection.QueryAsync<string>(
                @"SELECT lifecycle || ':' || paid::text FROM sales_invoice
                  WHERE customer_id = @CustomerId ORDER BY number", new { CustomerId = s.AlphaId })).ToArray();
            Assert.Equal(new[] { "posted:0.00", "posted:4000.00", "cancelled:0.00", "draft:0.00", "posted:18000.00" }, rows);
        }
    }
}
