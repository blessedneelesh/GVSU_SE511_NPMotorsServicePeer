using System.Data;
using Dapper;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.Models.Enums;
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
            _vehicleRepository = new VehicleRepository(_repositoryContext);
            _vendorRepository = new VendorRepository(_repositoryContext);
            _customerRepository = new CustomerRepository(_repositoryContext);
            _employeeRepository = new EmployeeRepository(_repositoryContext);
        }

        public Task InitializeAsync() => Task.CompletedTask;

        public async Task DisposeAsync()
        {
            using IDbConnection connection = _repositoryContext.GetDatabaseConnection();
            foreach (var id in _invoiceIds)
            {
                await connection.ExecuteAsync("UPDATE vehicle SET invoice_id = NULL WHERE invoice_id = @Id", new { Id = id });
                await connection.ExecuteAsync("DELETE FROM sales_invoice WHERE id = @Id", new { Id = id });
            }
            foreach (var id in _vehicleIds)
                await _vehicleRepository.DeleteVehicle(id);
            foreach (var id in _vendorIds)
                await _vendorRepository.DeleteVendor(id);
            foreach (var id in _customerIds)
                await _customerRepository.DeleteCustomer(id);
            foreach (var id in _employeeIds)
                await _employeeRepository.DeleteEmployee(id);
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
        public async Task AddSalesInvoice_VehicleAlreadyInvoiced_Throws()
        {
            var dto = await ValidDtoAsync();
            await AddAsync(dto);

            var ex = await Assert.ThrowsAsync<ArgumentException>(() => _repository.AddSalesInvoice(dto));

            Assert.Equal("This vehicle already has a sales invoice.", ex.Message);
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
        public async Task PostSalesInvoice_VehicleAlreadyHasDraft_Throws()
        {
            var dto = await ValidDtoAsync();
            await AddAsync(dto);

            var ex = await Assert.ThrowsAsync<ArgumentException>(() => _repository.PostSalesInvoice(dto));

            Assert.Equal("This vehicle already has a sales invoice.", ex.Message);
            var vehicle = await _vehicleRepository.GetVehicleById(dto.Vehicle_Id);
            Assert.Equal(VehicleStatus.Available, vehicle!.Status);
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
    }
}
