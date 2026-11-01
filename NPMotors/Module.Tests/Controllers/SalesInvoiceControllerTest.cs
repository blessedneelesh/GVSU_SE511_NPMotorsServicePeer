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
                Sale_Price = 17500m,
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

            Assert.Equal(18000m, body!.Total);
            Assert.Equal(0m, body.Paid);
            Assert.Equal(18000m, body.Balance);
        }

        [Fact]
        public async Task AddSalesInvoice_Valid_ReturnsVehicleDetailsAndNumber()
        {
            var response = await PostAsync(await ValidDtoAsync());
            var body = await ReadAsync<SalesInvoiceResponseDto>(response);

            Assert.Equal("Honda", body!.Make);
            Assert.Equal("Civic", body.Model);
            Assert.Equal(18000m, body.Selling_Price);
            Assert.Equal(15250m, body.Vehicle_Total_Cost);
            Assert.False(string.IsNullOrWhiteSpace(body.Vin));
            Assert.Equal($"INV-{body.Id:D6}", body.Number);
        }

        [Fact]
        public async Task AddSalesInvoice_TwoInvoices_GetUniqueNumbers()
        {
            var first = await ReadAsync<SalesInvoiceResponseDto>(await PostAsync(await ValidDtoAsync()));
            var secondDto = await ValidDtoAsync();
            var second = await ReadAsync<SalesInvoiceResponseDto>(await PostAsync(secondDto));

            Assert.NotEqual(first!.Number, second!.Number);
        }

        [Fact]
        public async Task AddSalesInvoice_Valid_DoesNotChangeVehicleStatus()
        {
            var dto = await ValidDtoAsync();

            await PostAsync(dto);

            var vehicle = await _vehicleRepo.GetVehicleById(dto.Vehicle_Id);
            Assert.Equal(VehicleStatus.Available, vehicle!.Status);
        }

        [Fact]
        public async Task AddSalesInvoice_DueDateOmitted_Returns201()
        {
            var dto = await ValidDtoAsync();
            dto.Due_Date = null;

            var response = await PostAsync(dto);
            var body = await ReadAsync<SalesInvoiceResponseDto>(response);

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            Assert.Null(body!.Due_Date);
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
        public async Task AddSalesInvoice_UnknownSalesperson_Returns400()
        {
            var dto = await ValidDtoAsync();
            dto.Salesperson_Id = long.MaxValue;

            var response = await PostAsync(dto);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task AddSalesInvoice_DiscountExceedsSalePrice_Returns400()
        {
            var dto = await ValidDtoAsync();
            dto.Discount = dto.Sale_Price + 1m;

            var response = await PostAsync(dto);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("Discount cannot exceed the sale price.", await response.Content.ReadAsStringAsync());
        }

        [Fact]
        public async Task AddSalesInvoice_TotalZero_Returns400()
        {
            var dto = await ValidDtoAsync();
            dto.Tax = 0m;
            dto.Fees = 0m;
            dto.Discount = dto.Sale_Price;

            var response = await PostAsync(dto);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("greater than zero", await response.Content.ReadAsStringAsync());
        }

        [Fact]
        public async Task AddSalesInvoice_MissingRequiredIds_Returns400()
        {
            var response = await PostAsync(new { Invoice_Date = "2026-05-01", Sale_Price = 1000 });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task AddSalesInvoice_MissingInvoiceDate_Returns400()
        {
            var response = await PostAsync(new
            {
                Customer_Id = 1,
                Salesperson_Id = 1,
                Vehicle_Id = 1,
                Sale_Price = 1000
            });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Theory]
        [InlineData("Sale_Price")]
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
