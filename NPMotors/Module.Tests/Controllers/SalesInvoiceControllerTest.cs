using System.Net;
using System.Text;
using Dapper;
using Newtonsoft.Json;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.Models.Enums;
using NPMotors.Repository.Repositories;

namespace Module.Tests.Controllers
{
    public class SalesInvoiceControllerTest : TestBase, IAsyncLifetime
    {
        private VendorRepository _vendorRepo => new(_repositoryContext);
        private VehicleRepository _vehicleRepo => new(_repositoryContext);
        private CustomerRepository _customerRepo => new(_repositoryContext);
        private EmployeeRepository _employeeRepo => new(_repositoryContext);
        private readonly string _marker = $"itest-{Guid.NewGuid():N}".Substring(0, 12);
        private readonly List<long> _invoiceIds = new();
        private readonly List<long> _vehicleIds = new();
        private readonly List<long> _vendorIds = new();
        private readonly List<long> _customerIds = new();
        private readonly List<long> _employeeIds = new();

        public Task InitializeAsync() => Task.CompletedTask;

        public async Task DisposeAsync()
        {
            using var connection = _repositoryContext.GetDatabaseConnection();
            foreach (var id in _invoiceIds)
            {
                try
                {
                    await connection.ExecuteAsync("UPDATE vehicle SET invoice_id = NULL WHERE invoice_id = @Id", new { Id = id });
                    await connection.ExecuteAsync("DELETE FROM sales_invoice WHERE id = @Id", new { Id = id });
                }
                catch { }
            }
            foreach (var id in _vehicleIds)
            {
                try { await _vehicleRepo.DeleteVehicle(id); } catch { }
            }
            foreach (var id in _vendorIds)
            {
                try { await _vendorRepo.DeleteVendor(id); } catch { }
            }
            foreach (var id in _customerIds)
            {
                try { await _customerRepo.DeleteCustomer(id); } catch { }
            }
            foreach (var id in _employeeIds)
            {
                try { await _employeeRepo.DeleteEmployee(id); } catch { }
            }
        }

        private async Task<long> CreateCustomerAsync(Status status = Status.Active)
        {
            var customer = await _customerRepo.AddCustomer(new CustomerCreateDto
            {
                First_Name = _marker,
                Last_Name = "Buyer",
                Email = $"{_marker}@example.com",
                Status = status
            });
            _customerIds.Add(customer.Id);
            return customer.Id;
        }

        private async Task<long> CreateSalespersonAsync()
        {
            var employee = await _employeeRepo.AddEmployee(new EmployeeCreateDto
            {
                First_Name = _marker,
                Last_Name = "Seller",
                Department = "Sales",
                Commission_Rate = 5m,
                Status = Status.Active
            });
            _employeeIds.Add(employee.Id);
            return employee.Id;
        }

        private async Task<VehicleResponseDto> CreateVehicleAsync(string status = "available")
        {
            var vendor = await _vendorRepo.AddVendor(new VendorCreateDto
            {
                Name = $"{_marker} Vendor",
                Type = VendorType.Vehicles,
                Status = Status.Active
            });
            _vendorIds.Add(vendor.Id);

            var vehicle = await _vehicleRepo.AddVehicle(new VehicleCreateDto
            {
                Vendor_Id = vendor.Id,
                Vin = $"{_marker}{_vehicleIds.Count + 1}".ToUpperInvariant().PadRight(17, '0').Substring(0, 17),
                Make = "Honda",
                Model = "Civic",
                Purchase_Price = 15000m,
                Additional_Costs = 250m,
                Selling_Price = 18000m
            });
            _vehicleIds.Add(vehicle.Id);

            if (status != "available")
            {
                using var connection = _repositoryContext.GetDatabaseConnection();
                await connection.ExecuteAsync("UPDATE vehicle SET status = @Status WHERE id = @Id",
                    new { Status = status, Id = vehicle.Id });
            }
            return vehicle;
        }

        private async Task<SalesInvoiceCreateDto> ValidDtoAsync(string vehicleStatus = "available")
        {
            var customerId = await CreateCustomerAsync();
            var salespersonId = await CreateSalespersonAsync();
            var vehicle = await CreateVehicleAsync(vehicleStatus);
            return new SalesInvoiceCreateDto
            {
                Customer_Id = customerId,
                Salesperson_Id = salespersonId,
                Vehicle_Id = vehicle.Id,
                Invoice_Date = new DateTime(2026, 5, 1),
                Due_Date = new DateTime(2026, 5, 31),
                Tax = 1000m,
                Fees = 200m,
                Discount = 700m
            };
        }

        private static StringContent Json(object? body) =>
            new(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");

        private async Task<HttpResponseMessage> PostAsync(object body)
        {
            var response = await _testClient.PostAsync("SalesInvoice/AddSalesInvoice", Json(body));
            if (response.StatusCode == HttpStatusCode.Created)
            {
                var created = JsonConvert.DeserializeObject<SalesInvoiceResponseDto>(
                    await response.Content.ReadAsStringAsync());
                _invoiceIds.Add(created!.Id);
            }
            return response;
        }

        private static async Task<T?> ReadAsync<T>(HttpResponseMessage response) =>
            JsonConvert.DeserializeObject<T>(await response.Content.ReadAsStringAsync());

        [Fact]
        public async Task AddSalesInvoice_Valid_Returns201WithDraftInvoice()
        {
            var dto = await ValidDtoAsync();

            var response = await PostAsync(dto);
            var body = await ReadAsync<SalesInvoiceResponseDto>(response);

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            Assert.True(body!.Id > 0);
            Assert.Equal(InvoiceLifecycle.Draft, body.Lifecycle);
            Assert.Null(body.Payment_Status);
            Assert.Equal(dto.Customer_Id, body.Customer_Id);
            Assert.Equal(dto.Salesperson_Id, body.Salesperson_Id);
            Assert.Equal(dto.Vehicle_Id, body.Vehicle_Id);
        }

        [Fact]
        public async Task AddSalesInvoice_Valid_ReturnsCalculatedTotalPaidAndBalance()
        {
            var response = await PostAsync(await ValidDtoAsync());
            var body = await ReadAsync<SalesInvoiceResponseDto>(response);

            Assert.Equal(18500m, body!.Total);
            Assert.Equal(0m, body.Paid);
            Assert.Equal(18500m, body.Balance);
        }

        [Theory]
        [InlineData("reserved")]
        [InlineData("sold")]
        public async Task AddSalesInvoice_VehicleNotAvailable_Returns400(string status)
        {
            var dto = await ValidDtoAsync(status);

            var response = await PostAsync(dto);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("available", await response.Content.ReadAsStringAsync());
        }

        [Fact]
        public async Task AddSalesInvoice_InactiveCustomer_Returns400()
        {
            var dto = await ValidDtoAsync();
            dto.Customer_Id = await CreateCustomerAsync(Status.Inactive);

            var response = await PostAsync(dto);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task AddSalesInvoice_MissingRequiredIds_Returns400()
        {
            var response = await PostAsync(new { Invoice_Date = "2026-05-01" });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Theory]
        [InlineData("Tax")]
        [InlineData("Fees")]
        [InlineData("Discount")]
        public async Task AddSalesInvoice_NegativeAmount_Returns400(string field)
        {
            var dto = await ValidDtoAsync();
            var body = JsonConvert.DeserializeObject<Dictionary<string, object>>(JsonConvert.SerializeObject(dto))!;
            body[field] = -1;

            var response = await PostAsync(body);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task AddSalesInvoice_VehicleAlreadyInvoiced_Returns400()
        {
            var dto = await ValidDtoAsync();
            await PostAsync(dto);

            var response = await PostAsync(dto);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task AddSalesInvoice_NonJsonBody_Returns400()
        {
            var response = await _testClient.PostAsync("SalesInvoice/AddSalesInvoice",
                new StringContent("not json", Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        // ---- PostSalesInvoice ----

        private async Task<HttpResponseMessage> PostInvoiceAsync(object body)
        {
            var response = await _testClient.PostAsync("SalesInvoice/PostSalesInvoice", Json(body));
            if (response.StatusCode == HttpStatusCode.Created)
            {
                var posted = JsonConvert.DeserializeObject<SalesInvoiceResponseDto>(
                    await response.Content.ReadAsStringAsync());
                _invoiceIds.Add(posted!.Id);
            }
            return response;
        }

        [Fact]
        public async Task PostSalesInvoice_Valid_Returns201WithPostedUnpaidInvoice()
        {
            var dto = await ValidDtoAsync();

            var response = await PostInvoiceAsync(dto);
            var body = await ReadAsync<SalesInvoiceResponseDto>(response);

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            Assert.True(body!.Id > 0);
            Assert.Equal(InvoiceLifecycle.Posted, body.Lifecycle);
            Assert.Equal(PaymentStatus.Unpaid, body.Payment_Status);
            Assert.Equal(dto.Customer_Id, body.Customer_Id);
            Assert.Equal(dto.Vehicle_Id, body.Vehicle_Id);
        }

        [Fact]
        public async Task PostSalesInvoice_Valid_SalePriceIsVehicleSellingPriceAndTotalsCalculated()
        {
            var response = await PostInvoiceAsync(await ValidDtoAsync());
            var body = await ReadAsync<SalesInvoiceResponseDto>(response);

            Assert.Equal(18000m, body!.Sale_Price);
            Assert.Equal(18500m, body.Total);
            Assert.Equal(0m, body.Paid);
            Assert.Equal(18500m, body.Balance);
        }

        [Fact]
        public async Task PostSalesInvoice_IgnoresSalePriceInRequestBody()
        {
            var dto = await ValidDtoAsync();
            var body = JsonConvert.DeserializeObject<Dictionary<string, object>>(JsonConvert.SerializeObject(dto))!;
            body["Sale_Price"] = 1;

            var response = await PostInvoiceAsync(body);
            var posted = await ReadAsync<SalesInvoiceResponseDto>(response);

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            Assert.Equal(18000m, posted!.Sale_Price);
        }

        [Fact]
        public async Task PostSalesInvoice_Valid_MarksVehicleSoldAndLinksInvoice()
        {
            var dto = await ValidDtoAsync();

            var response = await PostInvoiceAsync(dto);
            var posted = await ReadAsync<SalesInvoiceResponseDto>(response);

            var vehicle = await _vehicleRepo.GetVehicleById(dto.Vehicle_Id);
            Assert.Equal(VehicleStatus.Sold, vehicle!.Status);
            using var connection = _repositoryContext.GetDatabaseConnection();
            var invoiceId = await connection.ExecuteScalarAsync<long?>(
                "SELECT invoice_id FROM vehicle WHERE id = @Id", new { Id = dto.Vehicle_Id });
            Assert.Equal(posted!.Id, invoiceId);
        }

        [Fact]
        public async Task PostSalesInvoice_SoldVehicleNoLongerInAvailableList()
        {
            var dto = await ValidDtoAsync();
            await PostInvoiceAsync(dto);

            var response = await _testClient.GetAsync($"Vehicle/GetAllVehicles?Status=available&SearchTerm={_marker}");
            var text = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.DoesNotContain($"\"id\":{dto.Vehicle_Id},", text.ToLowerInvariant());
        }

        [Fact]
        public async Task PostSalesInvoice_SameVehicleTwice_SecondReturns400()
        {
            var dto = await ValidDtoAsync();
            var first = await PostInvoiceAsync(dto);

            var second = await PostInvoiceAsync(dto);

            Assert.Equal(HttpStatusCode.Created, first.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
            Assert.Contains("Only available vehicles can be selected.", await second.Content.ReadAsStringAsync());
        }

        [Fact]
        public async Task PostSalesInvoice_InactiveCustomer_Returns400AndVehicleStaysAvailable()
        {
            var dto = await ValidDtoAsync();
            dto.Customer_Id = await CreateCustomerAsync(Status.Inactive);

            var response = await PostInvoiceAsync(dto);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var vehicle = await _vehicleRepo.GetVehicleById(dto.Vehicle_Id);
            Assert.Equal(VehicleStatus.Available, vehicle!.Status);
        }

        [Theory]
        [InlineData("GET", "SalesInvoice/GetAllSalesInvoices")]
        [InlineData("GET", "SalesInvoice/GetSalesInvoiceById?id=1")]
        [InlineData("PUT", "SalesInvoice/UpdateSalesInvoice?id=1")]
        [InlineData("DELETE", "SalesInvoice/DeleteSalesInvoice?id=1")]
        public async Task OtherEndpoints_AreNotMapped(string method, string url)
        {
            var response = await _testClient.SendAsync(new HttpRequestMessage(new HttpMethod(method), url));

            Assert.Contains(response.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
        }
    }
}
