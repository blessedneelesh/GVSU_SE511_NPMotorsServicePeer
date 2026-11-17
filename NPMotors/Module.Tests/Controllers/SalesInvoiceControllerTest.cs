using System.Net;
using System.Text;
using Dapper;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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
        // Only the CreateAndPost tests need a bank; it is created on first use and swept in DisposeAsync.
        private long _bankId;
        private long _accountId;
        private long _inactiveAccountId;

        public Task InitializeAsync() => Task.CompletedTask;

        public async Task DisposeAsync()
        {
            // Sweep by this run's unique marker (not by tracked ids), so rows a failed test never
            // tracked, such as a second invoice for the same vehicle, are removed too.
            // Order follows the foreign keys: receipts and ledger rows, AR, invoices, vehicles, vendors,
            // customers, employees, then the bank accounts and bank.
            using var connection = _repositoryContext.GetDatabaseConnection();
            var pattern = new { Pattern = _marker + "%", VinPattern = _marker.ToUpperInvariant() + "%", BankId = _bankId };
            const string markerVehicles =
                "SELECT id FROM vehicle WHERE vendor_id IN (SELECT id FROM vendor WHERE name LIKE @Pattern) OR vin LIKE @VinPattern";
            const string markerInvoices =
                "SELECT id FROM sales_invoice WHERE vehicle_id IN (" + markerVehicles + ") OR customer_id IN (SELECT id FROM customer WHERE first_name LIKE @Pattern)";
            const string markerAccounts = "SELECT id FROM bank_account WHERE bank_id = @BankId";
            await connection.ExecuteAsync($"DELETE FROM bank_receipt WHERE bank_account_id IN ({markerAccounts})", pattern);
            await connection.ExecuteAsync($"DELETE FROM bank_statement_line WHERE bank_account_id IN ({markerAccounts})", pattern);
            await connection.ExecuteAsync($@"DELETE FROM ""transaction"" WHERE bank_account_id IN ({markerAccounts})", pattern);
            await connection.ExecuteAsync($"DELETE FROM ar_invoice WHERE invoice_id IN ({markerInvoices})", pattern);
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
            await connection.ExecuteAsync("DELETE FROM bank_account WHERE bank_id = @BankId", pattern);
            await connection.ExecuteAsync("DELETE FROM bank WHERE id = @BankId", pattern);
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
        public async Task AddSalesInvoice_VehicleAlreadyHasDraft_Returns201ForSecondDraft()
        {
            // Story 12 AC5: a vehicle can be on several drafts; only one posted invoice is allowed.
            var dto = await ValidDtoAsync();
            await PostAsync(dto);

            var response = await PostAsync(dto);

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        [Fact]
        public async Task PostSalesInvoice_VehicleAlreadyPosted_Returns400()
        {
            var dto = await ValidDtoAsync();
            await PostInvoiceAsync(dto);

            var response = await PostInvoiceAsync(dto);

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

        // ---- CreateAndPost: add the invoice and post it in one transaction ----
        // Total: vehicle selling price 18000 + tax 1000 + fees 200 - discount 700 = 18500.

        private const decimal CapTotal = 18500m;
        private const decimal CapOpeningBalance = 500m;

        private async Task EnsureBankAsync()
        {
            if (_bankId != 0)
                return;

            using var connection = _repositoryContext.GetDatabaseConnection();
            _bankId = await connection.ExecuteScalarAsync<long>(
                "INSERT INTO bank (name) VALUES (@Name) RETURNING id", new { Name = $"{_marker} Bank" });
            _accountId = await InsertAccountAsync("Operating", "active");
            _inactiveAccountId = await InsertAccountAsync("Closed", "inactive");
        }

        private async Task<long> InsertAccountAsync(string name, string status)
        {
            using var connection = _repositoryContext.GetDatabaseConnection();
            return await connection.ExecuteScalarAsync<long>(
                @"INSERT INTO bank_account (bank_id, name, type, opening_balance, book_balance, status)
                  VALUES (@BankId, @Name, 'Checking', @Balance, @Balance, @Status) RETURNING id",
                new { BankId = _bankId, Name = $"{_marker} {name}", Balance = CapOpeningBalance, Status = status });
        }

        private Task<HttpResponseMessage> CreateAndPostAsync(object body) =>
            _testClient.PostAsync("SalesInvoice/CreateAndPost", Json(body));

        // The add-invoice fields and the post-invoice fields in one flat body, for a new customer,
        // salesperson and vehicle.
        private async Task<(JObject Body, long VehicleId)> CreateAndPostBodyAsync(object payment, Status customerStatus = Status.Active)
        {
            await EnsureBankAsync();
            var dto = await ValidDtoAsync();
            if (customerStatus != Status.Active)
                dto.Customer_Id = await CreateCustomerAsync(customerStatus);

            var body = JObject.FromObject(new
            {
                customer_Id = dto.Customer_Id, salesperson_Id = dto.Salesperson_Id, vehicle_Id = dto.Vehicle_Id,
                invoice_Date = "2026-11-13", due_Date = "2026-12-13", tax = 1000, fees = 200, discount = 700
            });
            body.Merge(JObject.FromObject(payment));
            return (body, dto.Vehicle_Id);
        }

        private object PaidInFullPayment(long? accountId = null, string method = "card") => new
        {
            paymentCondition = "paidInFull", amountReceived = CapTotal, paymentMethod = method,
            reference = "REF-1", bankAccountId = accountId ?? _accountId, receiptDate = "2026-11-08"
        };

        private object PartialPayment(decimal amount = 6000m) => new
        {
            paymentCondition = "partialPayment", amountReceived = amount, paymentMethod = "transfer",
            reference = "WIRE-77", bankAccountId = _accountId, receiptDate = "2026-11-08"
        };

        private static object CreditPayment() => new { paymentCondition = "credit", amountReceived = 0 };

        private static async Task<JObject> ReadJsonAsync(HttpResponseMessage response) =>
            JObject.Parse(await response.Content.ReadAsStringAsync());

        private async Task<T> QueryAsync<T>(string sql, object param)
        {
            using var connection = _repositoryContext.GetDatabaseConnection();
            return await connection.ExecuteScalarAsync<T>(sql, param) ?? default!;
        }

        private Task<decimal> BookBalanceAsync(long accountId) =>
            QueryAsync<decimal>("SELECT book_balance FROM bank_account WHERE id = @Id", new { Id = accountId });

        // A rejected request must leave no invoice at all (not even a Draft), and no receipt, AR or ledger rows.
        private async Task AssertNothingSavedAsync(long vehicleId)
        {
            Assert.Equal(0, await QueryAsync<int>("SELECT COUNT(*) FROM sales_invoice WHERE vehicle_id = @Id", new { Id = vehicleId }));
            Assert.Equal(0, await QueryAsync<int>(
                "SELECT COUNT(*) FROM sales_invoice WHERE customer_id IN (SELECT id FROM customer WHERE first_name = @Marker)",
                new { Marker = _marker }));
            Assert.Equal("available", await QueryAsync<string>("SELECT LOWER(status) FROM vehicle WHERE id = @Id", new { Id = vehicleId }));
            Assert.Equal(0, await QueryAsync<int>(
                @"SELECT COUNT(*) FROM ""transaction"" WHERE bank_account_id IN (SELECT id FROM bank_account WHERE bank_id = @BankId)",
                new { BankId = _bankId }));
            Assert.Equal(CapOpeningBalance, await BookBalanceAsync(_accountId));
        }

        [Fact]
        public async Task CreateAndPost_PaidInFull_Returns201WithPaidInvoiceAndReceiptButNoAr()
        {
            await EnsureBankAsync();
            var (body, _) = await CreateAndPostBodyAsync(PaidInFullPayment());

            var response = await CreateAndPostAsync(body);

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var created = (await ReadAsync<SalesInvoiceResponseDto>(response))!;
            Assert.True(created.Id > 0);
            Assert.Equal(CapTotal, created.Total);
            Assert.Equal(CapTotal, created.Paid);
            Assert.Equal(0m, created.Balance);
            Assert.Equal(PaymentStatus.Paid, created.Payment_Status);
            Assert.Equal(InvoiceLifecycle.Posted, created.Lifecycle);
            Assert.Null(created.Ar_Invoice_Id);
            Assert.NotNull(created.Bank_Receipt_Id);
        }

        [Fact]
        public async Task CreateAndPost_Partial_Returns201WithPartialInvoiceArAndReceipt()
        {
            await EnsureBankAsync();
            var (body, _) = await CreateAndPostBodyAsync(PartialPayment(6000m));

            var response = await CreateAndPostAsync(body);

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var created = (await ReadAsync<SalesInvoiceResponseDto>(response))!;
            Assert.Equal(6000m, created.Paid);
            Assert.Equal(12500m, created.Balance);
            Assert.Equal(PaymentStatus.Partial, created.Payment_Status);
            Assert.NotNull(created.Ar_Invoice_Id);
            Assert.NotNull(created.Bank_Receipt_Id);
        }

        [Fact]
        public async Task CreateAndPost_Credit_Returns201WithUnpaidInvoiceArAndNoReceipt()
        {
            var (body, _) = await CreateAndPostBodyAsync(CreditPayment());

            var response = await CreateAndPostAsync(body);

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var created = (await ReadAsync<SalesInvoiceResponseDto>(response))!;
            Assert.Equal(0m, created.Paid);
            Assert.Equal(CapTotal, created.Balance);
            Assert.Equal(PaymentStatus.Unpaid, created.Payment_Status);
            Assert.NotNull(created.Ar_Invoice_Id);
            Assert.Null(created.Bank_Receipt_Id);
        }

        [Fact]
        public async Task CreateAndPost_Returns201WithTheDocumentedFields()
        {
            await EnsureBankAsync();
            var (body, _) = await CreateAndPostBodyAsync(PartialPayment());

            var response = await CreateAndPostAsync(body);

            var json = await ReadJsonAsync(response);
            foreach (var name in new[] { "id", "number", "total", "paid", "balance", "payment_status", "lifecycle", "ar_invoice_id", "bank_receipt_id" })
                Assert.NotNull(json.GetValue(name, StringComparison.OrdinalIgnoreCase));
            Assert.Equal("partial", json.GetValue("payment_status", StringComparison.OrdinalIgnoreCase)!.ToString());
            Assert.Equal("posted", json.GetValue("lifecycle", StringComparison.OrdinalIgnoreCase)!.ToString());
        }

        [Fact]
        public async Task CreateAndPost_Paid_SavesInvoiceVehicleAndRaisesTheBookBalance()
        {
            await EnsureBankAsync();
            var (body, vehicleId) = await CreateAndPostBodyAsync(PaidInFullPayment());

            var response = await CreateAndPostAsync(body);

            var invoiceId = (await ReadAsync<SalesInvoiceResponseDto>(response))!.Id;
            Assert.Equal("posted", await QueryAsync<string>("SELECT lifecycle FROM sales_invoice WHERE id = @Id", new { Id = invoiceId }));
            Assert.Equal("sold", await QueryAsync<string>("SELECT LOWER(status) FROM vehicle WHERE id = @Id", new { Id = vehicleId }));
            Assert.Equal(CapOpeningBalance + CapTotal, await BookBalanceAsync(_accountId));
            Assert.Equal(1, await QueryAsync<int>("SELECT COUNT(*) FROM bank_receipt WHERE sales_invoice_id = @Id", new { Id = invoiceId }));
        }

        [Fact]
        public async Task CreateAndPost_NeverLeavesADraftBehind()
        {
            var (body, vehicleId) = await CreateAndPostBodyAsync(CreditPayment());

            await CreateAndPostAsync(body);

            Assert.Equal(0, await QueryAsync<int>(
                "SELECT COUNT(*) FROM sales_invoice WHERE vehicle_id = @Id AND LOWER(lifecycle) = 'draft'", new { Id = vehicleId }));
        }

        [Theory]
        [InlineData("cash")]
        [InlineData("card")]
        [InlineData("transfer")]
        [InlineData("check")]
        public async Task CreateAndPost_EachAllowedMethod_Returns201(string method)
        {
            await EnsureBankAsync();
            var (body, _) = await CreateAndPostBodyAsync(PaidInFullPayment(method: method));

            var response = await CreateAndPostAsync(body);

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        [Fact]
        public async Task CreateAndPost_UnknownCustomer_Returns400AndSavesNothing()
        {
            var (body, vehicleId) = await CreateAndPostBodyAsync(CreditPayment());
            body["customer_Id"] = long.MaxValue;

            var response = await CreateAndPostAsync(body);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("Customer does not exist", (await ReadJsonAsync(response))["message"]!.ToString());
            await AssertNothingSavedAsync(vehicleId);
        }

        [Fact]
        public async Task CreateAndPost_InactiveCustomer_Returns400AndSavesNothing()
        {
            var (body, vehicleId) = await CreateAndPostBodyAsync(CreditPayment(), Status.Inactive);

            var response = await CreateAndPostAsync(body);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("active customers", (await ReadJsonAsync(response))["message"]!.ToString());
            await AssertNothingSavedAsync(vehicleId);
        }

        [Fact]
        public async Task CreateAndPost_UnknownVehicle_Returns400()
        {
            var (body, _) = await CreateAndPostBodyAsync(CreditPayment());
            body["vehicle_Id"] = long.MaxValue;

            var response = await CreateAndPostAsync(body);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("Vehicle does not exist", (await ReadJsonAsync(response))["message"]!.ToString());
        }

        [Fact]
        public async Task CreateAndPost_NegativeTax_Returns400AndSavesNothing()
        {
            var (body, vehicleId) = await CreateAndPostBodyAsync(CreditPayment());
            body["tax"] = -1;

            var response = await CreateAndPostAsync(body);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            await AssertNothingSavedAsync(vehicleId);
        }

        [Fact]
        public async Task CreateAndPost_NegativeAmount_Returns400WithErrorsAndSavesNothing()
        {
            await EnsureBankAsync();
            var (body, vehicleId) = await CreateAndPostBodyAsync(PartialPayment(-1m));

            var response = await CreateAndPostAsync(body);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var errors = (await ReadJsonAsync(response))["errors"]!.ToObject<string[]>()!;
            Assert.Contains(errors, e => e.Contains("cannot be negative"));
            await AssertNothingSavedAsync(vehicleId);
        }

        [Fact]
        public async Task CreateAndPost_AmountAboveTotal_Returns400AndSavesNothing()
        {
            await EnsureBankAsync();
            var (body, vehicleId) = await CreateAndPostBodyAsync(new
            {
                paymentCondition = "paidInFull", amountReceived = CapTotal + 0.01m, paymentMethod = "card", bankAccountId = _accountId
            });

            var response = await CreateAndPostAsync(body);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var errors = (await ReadJsonAsync(response))["errors"]!.ToObject<string[]>()!;
            Assert.Contains(errors, e => e.Contains("cannot exceed the invoice total"));
            await AssertNothingSavedAsync(vehicleId);
        }

        [Fact]
        public async Task CreateAndPost_PaidInFullWithPartialAmount_Returns400AndSavesNothing()
        {
            await EnsureBankAsync();
            var (body, vehicleId) = await CreateAndPostBodyAsync(new
            {
                paymentCondition = "paidInFull", amountReceived = 6000, paymentMethod = "card", bankAccountId = _accountId
            });

            var response = await CreateAndPostAsync(body);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            await AssertNothingSavedAsync(vehicleId);
        }

        [Fact]
        public async Task CreateAndPost_CreditWithAmount_Returns400AndSavesNothing()
        {
            var (body, vehicleId) = await CreateAndPostBodyAsync(new { paymentCondition = "credit", amountReceived = 100 });

            var response = await CreateAndPostAsync(body);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            await AssertNothingSavedAsync(vehicleId);
        }

        [Fact]
        public async Task CreateAndPost_MissingPaymentMethodWithAmount_Returns400AndSavesNothing()
        {
            await EnsureBankAsync();
            var (body, vehicleId) = await CreateAndPostBodyAsync(new
            {
                paymentCondition = "paidInFull", amountReceived = CapTotal, bankAccountId = _accountId
            });

            var response = await CreateAndPostAsync(body);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            await AssertNothingSavedAsync(vehicleId);
        }

        [Fact]
        public async Task CreateAndPost_AmountWithoutBankAccount_Returns400AndSavesNothing()
        {
            var (body, vehicleId) = await CreateAndPostBodyAsync(new
            {
                paymentCondition = "paidInFull", amountReceived = CapTotal, paymentMethod = "card"
            });

            var response = await CreateAndPostAsync(body);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var errors = (await ReadJsonAsync(response))["errors"]!.ToObject<string[]>()!;
            Assert.Contains(errors, e => e.Contains("bank account is required"));
            await AssertNothingSavedAsync(vehicleId);
        }

        [Fact]
        public async Task CreateAndPost_InactiveBankAccount_Returns400AndSavesNothing()
        {
            await EnsureBankAsync();
            var (body, vehicleId) = await CreateAndPostBodyAsync(PaidInFullPayment(accountId: _inactiveAccountId));

            var response = await CreateAndPostAsync(body);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var errors = (await ReadJsonAsync(response))["errors"]!.ToObject<string[]>()!;
            Assert.Contains(errors, e => e.Contains("must be active"));
            await AssertNothingSavedAsync(vehicleId);
        }

        [Fact]
        public async Task CreateAndPost_VehicleAlreadySold_Returns400AndCreatesNoSecondInvoice()
        {
            var (body, vehicleId) = await CreateAndPostBodyAsync(CreditPayment());
            await CreateAndPostAsync(body);

            var response = await CreateAndPostAsync(body);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(1, await QueryAsync<int>("SELECT COUNT(*) FROM sales_invoice WHERE vehicle_id = @Id", new { Id = vehicleId }));
        }

        [Fact]
        public async Task CreateAndPost_NonJsonBody_Returns400()
        {
            var response = await _testClient.PostAsync("SalesInvoice/CreateAndPost",
                new StringContent("not json", Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task CreateAndPost_GetIsNotAllowed_Returns405()
        {
            var response = await _testClient.GetAsync("SalesInvoice/CreateAndPost");

            Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        }

        // ---- UpdateDraft: edit a Draft with the add-invoice fields ----
        // Total: vehicle selling price 18000 + tax + fees - discount.

        private Task<HttpResponseMessage> UpdateDraftAsync(object id, object? body) =>
            _testClient.PutAsync($"SalesInvoice/UpdateDraft?id={id}", Json(body));

        private async Task<(string Lifecycle, long CustomerId, long VehicleId, decimal Tax, decimal Total, decimal Balance)> InvoiceRowAsync(long id)
        {
            using var connection = _repositoryContext.GetDatabaseConnection();
            return await connection.QuerySingleAsync<(string, long, long, decimal, decimal, decimal)>(
                "SELECT lifecycle, customer_id, vehicle_id, tax, total, balance FROM sales_invoice WHERE id = @Id", new { Id = id });
        }

        [Fact]
        public async Task UpdateDraft_Valid_Returns200WithTheEditedDraft()
        {
            var draft = await NewDraftAsync();
            var edit = await ValidDtoAsync();
            edit.Tax = 2000m;
            edit.Fees = 100m;
            edit.Discount = 300m;

            var response = await UpdateDraftAsync(draft.Id, edit);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = (await ReadAsync<SalesInvoiceResponseDto>(response))!;
            Assert.Equal(draft.Id, body.Id);
            Assert.Equal(draft.Number, body.Number);
            Assert.Equal(InvoiceLifecycle.Draft, body.Lifecycle);
            Assert.Equal(edit.Customer_Id, body.Customer_Id);
            Assert.Equal(edit.Vehicle_Id, body.Vehicle_Id);
            Assert.Equal(2000m, body.Tax);
            Assert.Equal(19800m, body.Total);
            Assert.Equal(0m, body.Paid);
            Assert.Equal(19800m, body.Balance);
        }

        [Fact]
        public async Task UpdateDraft_Valid_SavesTheEditAndKeepsItADraft()
        {
            var draft = await NewDraftAsync();
            var edit = await ValidDtoAsync();
            edit.Tax = 2000m;

            await UpdateDraftAsync(draft.Id, edit);

            var row = await InvoiceRowAsync(draft.Id);
            Assert.Equal("draft", row.Lifecycle);
            Assert.Equal(edit.Customer_Id, row.CustomerId);
            Assert.Equal(edit.Vehicle_Id, row.VehicleId);
            Assert.Equal(2000m, row.Tax);
            Assert.Equal(row.Total, row.Balance);
        }

        [Fact]
        public async Task UpdateDraft_DoesNotMarkTheVehicleSold()
        {
            var draft = await NewDraftAsync();
            var edit = await ValidDtoAsync();

            await UpdateDraftAsync(draft.Id, edit);

            using var connection = _repositoryContext.GetDatabaseConnection();
            Assert.Equal("available", await connection.ExecuteScalarAsync<string>(
                "SELECT LOWER(status) FROM vehicle WHERE id = @Id", new { Id = edit.Vehicle_Id }));
        }

        [Fact]
        public async Task UpdateDraft_AcceptsTheCreateAndPostBodyAndIgnoresThePaymentFields()
        {
            await EnsureBankAsync();
            var draft = await NewDraftAsync();
            var (body, _) = await CreateAndPostBodyAsync(PaidInFullPayment());

            var response = await UpdateDraftAsync(draft.Id, body);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var updated = (await ReadAsync<SalesInvoiceResponseDto>(response))!;
            Assert.Equal(InvoiceLifecycle.Draft, updated.Lifecycle);
            Assert.Equal(0m, updated.Paid);
            Assert.Equal(0, await QueryAsync<int>("SELECT COUNT(*) FROM bank_receipt WHERE sales_invoice_id = @Id", new { Id = draft.Id }));
            Assert.Equal(CapOpeningBalance, await BookBalanceAsync(_accountId));
        }

        [Fact]
        public async Task UpdateDraft_UnknownId_Returns404()
        {
            var response = await UpdateDraftAsync(long.MaxValue, await ValidDtoAsync());

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        [Fact]
        public async Task UpdateDraft_PostedInvoice_Returns409AndChangesNothing()
        {
            var posted = await NewPostedAsync();
            var before = await InvoiceRowAsync(posted.Id);
            var edit = await ValidDtoAsync();
            edit.Tax = 9999m;

            var response = await UpdateDraftAsync(posted.Id, edit);

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Contains("Draft", (await ReadJsonAsync(response))["message"]!.ToString());
            Assert.Equal(before, await InvoiceRowAsync(posted.Id));
        }

        [Fact]
        public async Task UpdateDraft_CancelledInvoice_Returns409()
        {
            var posted = await NewPostedAsync();
            await CancelAsync(posted.Id.ToString(), new { reason = "test" });

            var response = await UpdateDraftAsync(posted.Id, await ValidDtoAsync());

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        }

        [Fact]
        public async Task UpdateDraft_InactiveCustomer_Returns400AndChangesNothing()
        {
            var draft = await NewDraftAsync();
            var before = await InvoiceRowAsync(draft.Id);
            var edit = await ValidDtoAsync();
            edit.Customer_Id = await CreateCustomerAsync(Status.Inactive);

            var response = await UpdateDraftAsync(draft.Id, edit);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("active customers", (await ReadJsonAsync(response))["message"]!.ToString());
            Assert.Equal(before, await InvoiceRowAsync(draft.Id));
        }

        [Fact]
        public async Task UpdateDraft_VehicleNotAvailable_Returns400AndChangesNothing()
        {
            var draft = await NewDraftAsync();
            var before = await InvoiceRowAsync(draft.Id);
            var edit = await ValidDtoAsync("sold");

            var response = await UpdateDraftAsync(draft.Id, edit);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(before, await InvoiceRowAsync(draft.Id));
        }

        [Theory]
        [InlineData("tax")]
        [InlineData("fees")]
        [InlineData("discount")]
        public async Task UpdateDraft_NegativeAmount_Returns400(string field)
        {
            var draft = await NewDraftAsync();
            var edit = JObject.FromObject(await ValidDtoAsync());
            edit[char.ToUpper(field[0]) + field[1..]] = -1;

            var response = await UpdateDraftAsync(draft.Id, edit);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task UpdateDraft_MissingRequiredIds_Returns400()
        {
            var draft = await NewDraftAsync();

            var response = await UpdateDraftAsync(draft.Id, new { invoice_Date = "2026-05-01" });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task UpdateDraft_NonNumericId_Returns400()
        {
            var response = await UpdateDraftAsync("abc", await ValidDtoAsync());

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task UpdateDraft_NonJsonBody_Returns400()
        {
            var draft = await NewDraftAsync();

            var response = await _testClient.PutAsync($"SalesInvoice/UpdateDraft?id={draft.Id}",
                new StringContent("not json", Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task UpdateDraft_PostIsNotAllowed_Returns405()
        {
            var draft = await NewDraftAsync();

            var response = await _testClient.PostAsync($"SalesInvoice/UpdateDraft?id={draft.Id}", Json(await ValidDtoAsync()));

            Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        }

        [Fact]
        public async Task UpdateDraft_ThenPostInvoice_PostsTheEditedDraft()
        {
            await EnsureBankAsync();
            var draft = await NewDraftAsync();
            var edit = await ValidDtoAsync();
            edit.Tax = 1000m; edit.Fees = 200m; edit.Discount = 700m;
            await UpdateDraftAsync(draft.Id, edit);

            var response = await _testClient.PostAsync($"SalesInvoice/PostInvoice?id={draft.Id}", Json(PaidInFullPayment()));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var posted = (await ReadAsync<SalesInvoiceResponseDto>(response))!;
            Assert.Equal(CapTotal, posted.Total);
            Assert.Equal(edit.Vehicle_Id, posted.Vehicle_Id);
            Assert.Equal(InvoiceLifecycle.Posted, posted.Lifecycle);
        }

        [Theory]
        [InlineData("PUT", "SalesInvoice/UpdateSalesInvoice?id=1")]
        [InlineData("DELETE", "SalesInvoice/DeleteSalesInvoice?id=1")]
        public async Task OtherEndpoints_AreNotMapped(string method, string url)
        {
            var response = await _testClient.SendAsync(new HttpRequestMessage(new HttpMethod(method), url));

            Assert.Contains(response.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
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
            var salespersonId = await CreateSalespersonAsync();
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
            var vehicle = await new VehicleRepository(_repositoryContext).AddVehicle(new VehicleCreateDto
            {
                Vendor_Id = _byCustomerVendorId,
                Vin = $"{_marker}B{++_byCustomerVehicleCount}".ToUpperInvariant().PadRight(17, '0').Substring(0, 17),
                Make = make, Model = model, Year = year,
                Purchase_Price = 15000m, Additional_Costs = 250m, Selling_Price = sellingPrice
            });
            var invoice = await new SalesInvoiceRepository(_repositoryContext).AddSalesInvoice(new SalesInvoiceCreateDto
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

        private async Task<(HttpResponseMessage Response, JObject Json)> GetByCustomerAsync(string query)
        {
            var response = await _testClient.GetAsync($"SalesInvoice/GetSalesInvoiceByCustomer?{query}");
            var text = await response.Content.ReadAsStringAsync();
            return (response, response.IsSuccessStatusCode ? JObject.Parse(text) : new JObject());
        }

        private static JToken Prop(JToken token, string name) =>
            ((JObject)token).GetValue(name, StringComparison.OrdinalIgnoreCase)!;

        private static JArray Items(JObject json) => (JArray)Prop(json, "items");

        private static string[] Numbers(JObject json) =>
            Items(json).Select(i => Prop(i, "invoice_Number").ToString()).ToArray();

        // ---- 200, response shape ----

        [Fact]
        public async Task GetSalesInvoiceByCustomer_Returns200WithItems()
        {
            var s = await SeedByCustomerAsync();
            var (response, json) = await GetByCustomerAsync($"customerId={s.AlphaId}");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(4, Items(json).Count);
        }

        [Fact]
        public async Task GetSalesInvoiceByCustomer_IsNotPaged_NoMetaData()
        {
            var s = await SeedByCustomerAsync();
            var (_, json) = await GetByCustomerAsync($"customerId={s.AlphaId}");

            Assert.Null(json.GetValue("metaData", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task GetSalesInvoiceByCustomer_PagingParametersAreIgnored()
        {
            var s = await SeedByCustomerAsync();
            var (_, json) = await GetByCustomerAsync($"customerId={s.AlphaId}&PageNumber=2&PageSize=1");

            Assert.Equal(4, Items(json).Count);
        }

        [Fact]
        public async Task GetSalesInvoiceByCustomer_EachItemShowsTheDocumentedColumns()
        {
            var s = await SeedByCustomerAsync();
            var (_, json) = await GetByCustomerAsync($"customerId={s.AlphaId}");

            var names = ((JObject)Items(json)[0]).Properties().Select(p => p.Name.ToLowerInvariant()).OrderBy(n => n).ToArray();
            Assert.Equal(new[] { "amount", "invoice_date", "invoice_number", "paid", "payment_status", "salesperson", "vehicle" }, names);
        }

        [Fact]
        public async Task GetSalesInvoiceByCustomer_ReturnsTheStoredValues()
        {
            var s = await SeedByCustomerAsync();
            var (_, json) = await GetByCustomerAsync($"customerId={s.AlphaId}");

            var row = Items(json).Single(i => Prop(i, "invoice_Number").ToString() == s.Number("X2"));
            Assert.Equal(new DateTime(2026, 5, 1), ((DateTime)Prop(row, "invoice_Date")).Date);
            Assert.Equal("2020 Honda Civic", Prop(row, "vehicle").ToString());
            Assert.Equal($"{_marker} Seller", Prop(row, "salesperson").ToString());
            Assert.Equal(18000m, (decimal)Prop(row, "amount"));
            Assert.Equal(4000m, (decimal)Prop(row, "paid"));
            Assert.Equal("partial", Prop(row, "payment_Status").ToString());
        }

        [Fact]
        public async Task GetSalesInvoiceByCustomer_AmountIncludesTax()
        {
            var s = await SeedByCustomerAsync();
            var (_, json) = await GetByCustomerAsync($"customerId={s.AlphaId}");

            var row = Items(json).Single(i => Prop(i, "invoice_Number").ToString() == s.Number("X1"));
            Assert.Equal(18500m, (decimal)Prop(row, "amount"));
        }

        // ---- Lifecycle, customer, order ----

        [Fact]
        public async Task GetSalesInvoiceByCustomer_DraftIsNotListed()
        {
            var s = await SeedByCustomerAsync();
            var (_, json) = await GetByCustomerAsync($"customerId={s.AlphaId}");

            Assert.DoesNotContain(s.Number("X4"), Numbers(json));
        }

        [Fact]
        public async Task GetSalesInvoiceByCustomer_CancelledIsListed()
        {
            var s = await SeedByCustomerAsync();
            var (_, json) = await GetByCustomerAsync($"customerId={s.AlphaId}");

            Assert.Contains(s.Number("X3"), Numbers(json));
        }

        [Fact]
        public async Task GetSalesInvoiceByCustomer_OnlyThatCustomersInvoices()
        {
            var s = await SeedByCustomerAsync();
            var (_, json) = await GetByCustomerAsync($"customerId={s.GammaId}");

            Assert.Equal(new[] { s.Number("G1") }, Numbers(json));
        }

        [Fact]
        public async Task GetSalesInvoiceByCustomer_NewestFirstThenNumber()
        {
            var s = await SeedByCustomerAsync();
            var (_, json) = await GetByCustomerAsync($"customerId={s.AlphaId}");

            Assert.Equal(new[] { s.Number("X2"), s.Number("X5"), s.Number("X3"), s.Number("X1") }, Numbers(json));
        }

        // ---- Empty results are 200, not 404 ----

        [Fact]
        public async Task GetSalesInvoiceByCustomer_CustomerWithoutInvoices_Returns200WithEmptyItems()
        {
            var s = await SeedByCustomerAsync();
            var (response, json) = await GetByCustomerAsync($"customerId={s.BetaId}");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Empty(Items(json));
        }

        [Fact]
        public async Task GetSalesInvoiceByCustomer_UnknownCustomer_Returns200WithEmptyItems()
        {
            var (response, json) = await GetByCustomerAsync($"customerId={long.MaxValue}");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Empty(Items(json));
        }

        [Fact]
        public async Task GetSalesInvoiceByCustomer_NoCustomerId_Returns200WithEmptyItems()
        {
            var (response, json) = await GetByCustomerAsync(string.Empty);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Empty(Items(json));
        }

        // ---- 400 from [ApiController] model binding ----

        [Fact]
        public async Task GetSalesInvoiceByCustomer_NonNumericCustomerId_Returns400()
        {
            var (response, _) = await GetByCustomerAsync("customerId=abc");

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        // ---- Read-only ----

        [Theory]
        [InlineData("POST")]
        [InlineData("PUT")]
        [InlineData("DELETE")]
        public async Task GetSalesInvoiceByCustomer_WriteVerbs_Return405(string verb)
        {
            var request = new HttpRequestMessage(new HttpMethod(verb), "SalesInvoice/GetSalesInvoiceByCustomer")
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };

            var response = await _testClient.SendAsync(request);

            Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        }

        [Fact]
        public async Task GetSalesInvoiceByCustomer_ChangesNothing()
        {
            var s = await SeedByCustomerAsync();
            await GetByCustomerAsync($"customerId={s.AlphaId}");

            using var connection = _repositoryContext.GetDatabaseConnection();
            var rows = (await connection.QueryAsync<string>(
                @"SELECT lifecycle || ':' || paid::text FROM sales_invoice
                  WHERE customer_id = @CustomerId ORDER BY number", new { CustomerId = s.AlphaId })).ToArray();
            Assert.Equal(new[] { "posted:0.00", "posted:4000.00", "cancelled:0.00", "draft:0.00", "posted:18000.00" }, rows);
        }
    }
}
