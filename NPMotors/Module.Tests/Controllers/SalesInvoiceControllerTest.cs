using System.Net;
using System.Text;
using Dapper;
using Newtonsoft.Json;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.Models.Enums;
using NPMotors.Repository.Repositories;
using NPMotors.Repository.RequestFeatures;

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
            // Sweep by this run's unique marker (not by tracked ids), so rows a failed test never
            // tracked, such as a second invoice for the same vehicle, are removed too.
            // Order follows the foreign keys: invoices, vehicles, vendors, customers, employees.
            using var connection = _repositoryContext.GetDatabaseConnection();
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

        // ---- Story 9: GetAllSalesInvoices / GetSalesInvoiceById / CancelSalesInvoice ----

        private sealed class ListResponse
        {
            public List<SalesInvoiceResponseDto> Items { get; set; } = new();
            public MetaData MetaData { get; set; } = new();
        }

        private async Task<SalesInvoiceResponseDto> NewPostedAsync(DateTime? date = null)
        {
            var dto = await ValidDtoAsync();
            if (date is not null) dto.Invoice_Date = date.Value;
            var response = await PostInvoiceAsync(dto);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            return (await ReadAsync<SalesInvoiceResponseDto>(response))!;
        }

        private async Task<SalesInvoiceResponseDto> NewDraftAsync()
        {
            var response = await PostAsync(await ValidDtoAsync());
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            return (await ReadAsync<SalesInvoiceResponseDto>(response))!;
        }

        private Task<HttpResponseMessage> CancelAsync(string id, object? body) =>
            _testClient.PostAsync($"SalesInvoice/CancelSalesInvoice?id={id}", Json(body));

        private async Task<ListResponse> GetListAsync(string query)
        {
            var response = await _testClient.GetAsync($"SalesInvoice/GetAllSalesInvoices?SearchTerm={_marker}{query}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await ReadAsync<ListResponse>(response))!;
        }

        [Fact]
        public async Task GetAllSalesInvoices_Returns200WithItemsAndMetaData()
        {
            var posted = await NewPostedAsync();

            var response = await _testClient.GetAsync($"SalesInvoice/GetAllSalesInvoices?SearchTerm={_marker}");
            var text = await response.Content.ReadAsStringAsync();
            var body = JsonConvert.DeserializeObject<ListResponse>(text)!;

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("\"items\"", text);
            Assert.Contains("\"metaData\"", text);
            Assert.Equal(posted.Id, Assert.Single(body.Items).Id);
            Assert.Equal(1, body.MetaData.TotalCount);
        }

        [Fact]
        public async Task GetAllSalesInvoices_ItemsHaveCalculatedTotalAndBalance()
        {
            await NewPostedAsync();

            var item = Assert.Single((await GetListAsync("")).Items);

            Assert.Equal(18500m, item.Total);
            Assert.Equal(18500m, item.Balance);
        }

        [Fact]
        public async Task GetAllSalesInvoices_BindsLifecycleAndStatusFilters()
        {
            var draft = await NewDraftAsync();
            var posted = await NewPostedAsync();

            var drafts = await GetListAsync("&Lifecycle=draft");
            var unpaid = await GetListAsync("&Lifecycle=posted&Status=unpaid");
            var paid = await GetListAsync("&Status=paid");

            Assert.Equal(draft.Id, Assert.Single(drafts.Items).Id);
            Assert.Equal(posted.Id, Assert.Single(unpaid.Items).Id);
            Assert.Empty(paid.Items);
        }

        [Fact]
        public async Task GetAllSalesInvoices_BindsDateRange()
        {
            await NewPostedAsync(new DateTime(2026, 5, 1));
            var june = await NewPostedAsync(new DateTime(2026, 6, 1));

            var result = await GetListAsync("&FromDate=2026-05-15&ToDate=2026-06-30");

            Assert.Equal(june.Id, Assert.Single(result.Items).Id);
        }

        [Fact]
        public async Task GetAllSalesInvoices_BindsPaging()
        {
            await NewPostedAsync(new DateTime(2026, 5, 1));
            await NewPostedAsync(new DateTime(2026, 6, 1));

            var result = await GetListAsync("&PageNumber=2&PageSize=1");

            Assert.Single(result.Items);
            Assert.Equal(2, result.MetaData.TotalCount);
            Assert.Equal(2, result.MetaData.CurrentPage);
            Assert.Equal(1, result.MetaData.PageSize);
            Assert.Equal(2, result.MetaData.TotalPages);
        }

        [Fact]
        public async Task GetAllSalesInvoices_NoMatches_Returns200WithEmptyItems()
        {
            var response = await _testClient.GetAsync($"SalesInvoice/GetAllSalesInvoices?SearchTerm={_marker}-none");
            var body = await ReadAsync<ListResponse>(response);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Empty(body!.Items);
            Assert.Equal(0, body.MetaData.TotalCount);
        }

        [Fact]
        public async Task GetSalesInvoiceById_Existing_Returns200WithCalculatedFields()
        {
            var posted = await NewPostedAsync();

            var response = await _testClient.GetAsync($"SalesInvoice/GetSalesInvoiceById?id={posted.Id}");
            var body = await ReadAsync<SalesInvoiceResponseDto>(response);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(posted.Id, body!.Id);
            Assert.Equal($"{_marker} Buyer", body.Customer_Name);
            Assert.Equal("Honda", body.Make);
            Assert.Equal(18500m, body.Total);
            Assert.Equal(18500m, body.Balance);
        }

        [Fact]
        public async Task GetSalesInvoiceById_Unknown_Returns404()
        {
            var response = await _testClient.GetAsync($"SalesInvoice/GetSalesInvoiceById?id={long.MaxValue}");

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        [Fact]
        public async Task GetSalesInvoiceById_NonNumericId_Returns400()
        {
            var response = await _testClient.GetAsync("SalesInvoice/GetSalesInvoiceById?id=abc");

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task CancelSalesInvoice_Posted_Returns200WithCancelledInvoice()
        {
            var posted = await NewPostedAsync();

            var response = await CancelAsync(posted.Id.ToString(), new { Reason = "Customer changed their mind" });
            var body = await ReadAsync<SalesInvoiceResponseDto>(response);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(posted.Id, body!.Id);
            Assert.Equal(InvoiceLifecycle.Cancelled, body.Lifecycle);
            Assert.Equal("Customer changed their mind", body.Cancel_Reason);
            Assert.Equal(18500m, body.Total);
            Assert.Equal(18500m, body.Balance);
        }

        [Fact]
        public async Task CancelSalesInvoice_ReturnsVehicleToAvailableInventory()
        {
            var posted = await NewPostedAsync();

            await CancelAsync(posted.Id.ToString(), new { Reason = "Mistake" });

            var vehicle = await _vehicleRepo.GetVehicleById(posted.Vehicle_Id);
            Assert.Equal(VehicleStatus.Available, vehicle!.Status);
            var list = await _testClient.GetAsync($"Vehicle/GetAllVehicles?Status=available&SearchTerm={_marker}");
            Assert.Contains($"\"id\":{posted.Vehicle_Id},", (await list.Content.ReadAsStringAsync()).ToLowerInvariant());
        }

        [Fact]
        public async Task CancelSalesInvoice_InvoiceStaysInListAndCanBeOpened()
        {
            var posted = await NewPostedAsync();
            await CancelAsync(posted.Id.ToString(), new { Reason = "Mistake" });

            var list = await GetListAsync("&Lifecycle=cancelled");
            var open = await _testClient.GetAsync($"SalesInvoice/GetSalesInvoiceById?id={posted.Id}");

            Assert.Equal(posted.Id, Assert.Single(list.Items).Id);
            Assert.Equal(HttpStatusCode.OK, open.StatusCode);
        }

        [Fact]
        public async Task CancelSalesInvoice_UnknownId_Returns404()
        {
            var response = await CancelAsync(long.MaxValue.ToString(), new { Reason = "Mistake" });

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        [Fact]
        public async Task CancelSalesInvoice_Draft_Returns400()
        {
            var draft = await NewDraftAsync();

            var response = await CancelAsync(draft.Id.ToString(), new { Reason = "Mistake" });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("Only Posted invoices can be cancelled.", await response.Content.ReadAsStringAsync());
        }

        [Fact]
        public async Task CancelSalesInvoice_AlreadyCancelled_Returns400()
        {
            var posted = await NewPostedAsync();
            await CancelAsync(posted.Id.ToString(), new { Reason = "First" });

            var response = await CancelAsync(posted.Id.ToString(), new { Reason = "Second" });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task CancelSalesInvoice_WithPayments_Returns400AndInvoiceStaysPosted()
        {
            var posted = await NewPostedAsync();
            using (var connection = _repositoryContext.GetDatabaseConnection())
                await connection.ExecuteAsync("UPDATE sales_invoice SET paid = 100 WHERE id = @Id", new { posted.Id });

            var response = await CancelAsync(posted.Id.ToString(), new { Reason = "Mistake" });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("payments", await response.Content.ReadAsStringAsync());
            var open = await ReadAsync<SalesInvoiceResponseDto>(
                await _testClient.GetAsync($"SalesInvoice/GetSalesInvoiceById?id={posted.Id}"));
            Assert.Equal(InvoiceLifecycle.Posted, open!.Lifecycle);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public async Task CancelSalesInvoice_BlankReason_Returns400AndInvoiceStaysPosted(string reason)
        {
            var posted = await NewPostedAsync();

            var response = await CancelAsync(posted.Id.ToString(), new { Reason = reason });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var open = await ReadAsync<SalesInvoiceResponseDto>(
                await _testClient.GetAsync($"SalesInvoice/GetSalesInvoiceById?id={posted.Id}"));
            Assert.Equal(InvoiceLifecycle.Posted, open!.Lifecycle);
        }

        [Fact]
        public async Task CancelSalesInvoice_MissingReason_Returns400()
        {
            var posted = await NewPostedAsync();

            var response = await CancelAsync(posted.Id.ToString(), new { });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task CancelSalesInvoice_ReasonLongerThan500_Returns400()
        {
            var posted = await NewPostedAsync();

            var response = await CancelAsync(posted.Id.ToString(), new { Reason = new string('a', 501) });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task CancelSalesInvoice_Reason500Characters_Returns200()
        {
            var posted = await NewPostedAsync();

            var response = await CancelAsync(posted.Id.ToString(), new { Reason = new string('a', 500) });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        [Fact]
        public async Task CancelSalesInvoice_NonNumericId_Returns400()
        {
            var response = await CancelAsync("abc", new { Reason = "Mistake" });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task CancelSalesInvoice_NonJsonBody_Returns400()
        {
            var response = await _testClient.PostAsync("SalesInvoice/CancelSalesInvoice?id=1",
                new StringContent("not json", Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Theory]
        [InlineData("PUT", "SalesInvoice/UpdateSalesInvoice?id=1")]
        [InlineData("DELETE", "SalesInvoice/DeleteSalesInvoice?id=1")]
        public async Task OtherEndpoints_AreNotMapped(string method, string url)
        {
            var response = await _testClient.SendAsync(new HttpRequestMessage(new HttpMethod(method), url));

            Assert.Contains(response.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
        }
    }
}
