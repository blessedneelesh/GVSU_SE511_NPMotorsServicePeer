using System.Net;
using System.Text;
using Dapper;
using Newtonsoft.Json.Linq;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.Models.Enums;
using NPMotors.Repository.Repositories;

namespace Module.Tests.Controllers
{
    // GET /Ar/GetArAging (Story 17) against the test database. As-of date: 2026-11-08.
    // Customer Alpha: A1 due 2026-10-01 unpaid 1000 (38 days overdue), A2 due 2026-10-15 partial 1000/400 (24 days),
    //                 A3 due 2026-12-01 unpaid 1000 (0 days, not overdue), A6 fully paid (never listed).
    // Customer Beta:  B1 due 2026-10-20 unpaid 500 (19 days overdue).
    public class ArControllerTest : TestBase, IAsyncLifetime
    {
        private const string AsOf = "2026-11-08";

        private readonly string _marker = $"itest-{Guid.NewGuid():N}".Substring(0, 12);
        private long _alphaId;
        private long _betaId;
        private int _vehicleCount;

        public async Task InitializeAsync()
        {
            _alphaId = await AddCustomerAsync("Alpha");
            _betaId = await AddCustomerAsync("Beta");
            var salespersonId = (await new EmployeeRepository(_repositoryContext).AddEmployee(new EmployeeCreateDto
            {
                First_Name = _marker, Last_Name = "Seller", Department = "Sales", Commission_Rate = 5m, Status = Status.Active
            })).Id;
            var vendor = await new VendorRepository(_repositoryContext).AddVendor(new VendorCreateDto
            {
                Name = $"{_marker} Vendor", Type = VendorType.Vehicles, Status = Status.Active
            });

            await AddArAsync("A1", _alphaId, salespersonId, vendor.Id, new DateTime(2026, 10, 1), 1000m, 0m, "unpaid");
            await AddArAsync("A2", _alphaId, salespersonId, vendor.Id, new DateTime(2026, 10, 15), 1000m, 400m, "partial");
            await AddArAsync("A3", _alphaId, salespersonId, vendor.Id, new DateTime(2026, 12, 1), 1000m, 0m, "unpaid");
            await AddArAsync("A6", _alphaId, salespersonId, vendor.Id, new DateTime(2026, 9, 1), 1000m, 1000m, "paid");
            await AddArAsync("B1", _betaId, salespersonId, vendor.Id, new DateTime(2026, 10, 20), 500m, 0m, "unpaid");
        }

        public async Task DisposeAsync()
        {
            using var connection = _repositoryContext.GetDatabaseConnection();
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

        private async Task<long> AddCustomerAsync(string lastName) =>
            (await new CustomerRepository(_repositoryContext).AddCustomer(new CustomerCreateDto
            {
                First_Name = _marker, Last_Name = lastName, Email = $"{_marker}.{lastName}@example.com", Status = Status.Active
            })).Id;

        private async Task AddArAsync(string key, long customerId, long salespersonId, long vendorId,
            DateTime due, decimal amount, decimal paid, string status)
        {
            var vehicle = await new VehicleRepository(_repositoryContext).AddVehicle(new VehicleCreateDto
            {
                Vendor_Id = vendorId,
                Vin = $"{_marker}{++_vehicleCount}".ToUpperInvariant().PadRight(17, '0').Substring(0, 17),
                Make = "Honda", Model = "Civic",
                Purchase_Price = 15000m, Additional_Costs = 250m, Selling_Price = 18000m
            });
            var invoice = await new SalesInvoiceRepository(_repositoryContext).AddSalesInvoice(new SalesInvoiceCreateDto
            {
                Customer_Id = customerId, Salesperson_Id = salespersonId, Vehicle_Id = vehicle.Id,
                Invoice_Date = new DateTime(2026, 5, 1)
            });

            using var connection = _repositoryContext.GetDatabaseConnection();
            await connection.ExecuteAsync(
                @"INSERT INTO ar_invoice (invoice_id, number, date, due_date, amount, paid, balance, status, created_at, created_by)
                  VALUES (@InvoiceId, @Number, @Date, @Due, @Amount, @Paid, @Balance, @Status, NOW(), 'system')",
                new
                {
                    InvoiceId = invoice.Id, Number = $"{_marker}-{key}", Date = new DateTime(2026, 5, 1), Due = due,
                    Amount = amount, Paid = paid, Balance = amount - paid, Status = status
                });
        }

        private async Task<(HttpResponseMessage Response, JObject Json)> GetAsync(string query)
        {
            var response = await _testClient.GetAsync($"Ar/GetArAging?{query}");
            var text = await response.Content.ReadAsStringAsync();
            return (response, response.IsSuccessStatusCode ? JObject.Parse(text) : new JObject());
        }

        private static JToken Prop(JToken token, string name) =>
            ((JObject)token).GetValue(name, StringComparison.OrdinalIgnoreCase)!;

        private static JArray Items(JObject json) => (JArray)Prop(json, "items");

        private static JToken Row(JObject json, string number) =>
            Items(json).Single(i => Prop(i, "number").ToString() == number);

        private string Number(string key) => $"{_marker}-{key}";

        // ---- 200, response shape ----

        [Fact]
        public async Task GetArAging_Returns200WithItemsAndMetaData()
        {
            var (response, json) = await GetAsync($"AsOfDate={AsOf}&CustomerId={_alphaId}");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(3, Items(json).Count);
            var meta = Prop(json, "metaData");
            foreach (var name in new[] { "currentPage", "totalPages", "pageSize", "totalCount" })
                Assert.NotNull(((JObject)meta).GetValue(name, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(3, (int)Prop(meta, "totalCount"));
        }

        [Fact]
        public async Task GetArAging_EachItemShowsTheDocumentedColumns()
        {
            var (_, json) = await GetAsync($"AsOfDate={AsOf}&CustomerId={_alphaId}");

            foreach (var item in Items(json))
                foreach (var name in new[] { "id", "number", "customerId", "customer_Name", "invoice_Number", "due_Date",
                                             "amount", "paid", "balance", "status", "days_Overdue", "aging_Status" })
                    Assert.NotNull(((JObject)item).GetValue(name, StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task GetArAging_ReturnsTheStoredValuesAndCalculatedFields()
        {
            var (_, json) = await GetAsync($"AsOfDate={AsOf}&CustomerId={_alphaId}");

            var row = Row(json, Number("A2"));
            Assert.Equal($"{_marker} Alpha", Prop(row, "customer_Name").ToString());
            Assert.Equal(1000m, (decimal)Prop(row, "amount"));
            Assert.Equal(400m, (decimal)Prop(row, "paid"));
            Assert.Equal(600m, (decimal)Prop(row, "balance"));
            Assert.Equal("partial", Prop(row, "status").ToString());
            Assert.Equal(new DateTime(2026, 10, 15), (DateTime)Prop(row, "due_Date"));
            Assert.Equal(24, (int)Prop(row, "days_Overdue"));
            Assert.Equal("Overdue", Prop(row, "aging_Status").ToString());
        }

        // ---- AC 2, 3 ----

        [Fact]
        public async Task GetArAging_DaysOverdueIsAsOfMinusDueDate()
        {
            var (_, json) = await GetAsync($"AsOfDate={AsOf}&CustomerId={_alphaId}");

            Assert.Equal(38, (int)Prop(Row(json, Number("A1")), "days_Overdue"));
            Assert.Equal(24, (int)Prop(Row(json, Number("A2")), "days_Overdue"));
        }

        [Fact]
        public async Task GetArAging_NotYetDue_DaysOverdueZeroAndNotOverdue()
        {
            var (_, json) = await GetAsync($"AsOfDate={AsOf}&CustomerId={_alphaId}");

            var row = Row(json, Number("A3"));
            Assert.Equal(0, (int)Prop(row, "days_Overdue"));
            Assert.Equal("unpaid", Prop(row, "aging_Status").ToString());
        }

        [Fact]
        public async Task GetArAging_OpenArPastDue_ShowsOverdue()
        {
            var (_, json) = await GetAsync($"AsOfDate={AsOf}&CustomerId={_alphaId}");

            Assert.Equal("Overdue", Prop(Row(json, Number("A1")), "aging_Status").ToString());
        }

        [Fact]
        public async Task GetArAging_LaterAsOfDate_MakesTheSameRowOverdue()
        {
            var (_, json) = await GetAsync($"AsOfDate=2026-12-11&CustomerId={_alphaId}");

            var row = Row(json, Number("A3"));
            Assert.Equal(10, (int)Prop(row, "days_Overdue"));
            Assert.Equal("Overdue", Prop(row, "aging_Status").ToString());
        }

        [Fact]
        public async Task GetArAging_PaidArIsNotListed()
        {
            var (_, json) = await GetAsync($"AsOfDate={AsOf}&CustomerId={_alphaId}");

            Assert.DoesNotContain(Items(json), i => Prop(i, "number").ToString() == Number("A6"));
        }

        // ---- AC 4: query parameters bind ----

        [Fact]
        public async Task GetArAging_StatusOverdue_ReturnsOnlyOverdueRows()
        {
            var (_, json) = await GetAsync($"AsOfDate={AsOf}&CustomerId={_alphaId}&Status=Overdue");

            Assert.Equal(new[] { Number("A1"), Number("A2") }, Items(json).Select(i => Prop(i, "number").ToString()).ToArray());
            Assert.All(Items(json), i => Assert.Equal("Overdue", Prop(i, "aging_Status").ToString()));
        }

        [Fact]
        public async Task GetArAging_StatusUnpaid_ReturnsOnlyUnpaidNotPastDue()
        {
            var (_, json) = await GetAsync($"AsOfDate={AsOf}&CustomerId={_alphaId}&Status=Unpaid");

            Assert.Equal(Number("A3"), Prop(Assert.Single(Items(json)), "number").ToString());
        }

        [Fact]
        public async Task GetArAging_StatusPartial_NothingPartialNotPastDue_ReturnsEmptyItems()
        {
            var (response, json) = await GetAsync($"AsOfDate={AsOf}&CustomerId={_alphaId}&Status=Partial");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Empty(Items(json));
            Assert.Equal(0, (int)Prop(Prop(json, "metaData"), "totalCount"));
        }

        [Fact]
        public async Task GetArAging_CustomerId_ReturnsOnlyThatCustomer()
        {
            var (_, json) = await GetAsync($"AsOfDate={AsOf}&CustomerId={_betaId}");

            var row = Assert.Single(Items(json));
            Assert.Equal(Number("B1"), Prop(row, "number").ToString());
            Assert.Equal(_betaId, (long)Prop(row, "customerId"));
            Assert.Equal(19, (int)Prop(row, "days_Overdue"));
        }

        [Fact]
        public async Task GetArAging_StatusAndCustomerTogether_BothApply()
        {
            var (_, json) = await GetAsync($"AsOfDate={AsOf}&CustomerId={_betaId}&Status=Partial");

            Assert.Empty(Items(json));
        }

        [Fact]
        public async Task GetArAging_PageNumberAndPageSize_Bind()
        {
            var (_, json) = await GetAsync($"AsOfDate={AsOf}&CustomerId={_alphaId}&PageNumber=2&PageSize=2");

            var meta = Prop(json, "metaData");
            Assert.Equal(Number("A3"), Prop(Assert.Single(Items(json)), "number").ToString());
            Assert.Equal(2, (int)Prop(meta, "currentPage"));
            Assert.Equal(2, (int)Prop(meta, "pageSize"));
            Assert.Equal(3, (int)Prop(meta, "totalCount"));
            Assert.Equal(2, (int)Prop(meta, "totalPages"));
        }

        [Fact]
        public async Task GetArAging_PageSizeAboveTheCap_IsClamped()
        {
            var (_, json) = await GetAsync($"AsOfDate={AsOf}&CustomerId={_alphaId}&PageSize=5000");

            Assert.Equal(100, (int)Prop(Prop(json, "metaData"), "pageSize"));
        }

        [Fact]
        public async Task GetArAging_NoAsOfDate_UsesToday()
        {
            var (response, json) = await GetAsync($"CustomerId={_alphaId}&Status=Overdue");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            // Overdue means due before today: A1 (2026-10-01), A2 (2026-10-15) and A3 (2026-12-01) once today is later.
            var expected = new[] { new DateTime(2026, 10, 1), new DateTime(2026, 10, 15), new DateTime(2026, 12, 1) }
                .Count(d => d < DateTime.Today);
            Assert.Equal(expected, Items(json).Count);
        }

        [Fact]
        public async Task GetArAging_NoParameters_Returns200()
        {
            var (response, json) = await GetAsync("");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.NotNull(Prop(json, "items"));
        }

        // ---- 400 from [ApiController] model binding ----

        [Fact]
        public async Task GetArAging_NonNumericCustomerId_Returns400()
        {
            var (response, _) = await GetAsync("CustomerId=abc");

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task GetArAging_InvalidAsOfDate_Returns400()
        {
            var (response, _) = await GetAsync("AsOfDate=not-a-date");

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        // ---- Read-only story: no other actions ----

        [Theory]
        [InlineData("POST")]
        [InlineData("PUT")]
        [InlineData("DELETE")]
        public async Task GetArAging_WriteVerbs_Return405(string verb)
        {
            var request = new HttpRequestMessage(new HttpMethod(verb), "Ar/GetArAging")
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };

            var response = await _testClient.SendAsync(request);

            Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        }

        [Theory]
        [InlineData("POST", "Ar/AddAr")]
        [InlineData("PUT", "Ar/UpdateAr")]
        [InlineData("DELETE", "Ar/DeleteAr")]
        [InlineData("GET", "Ar/GetArById")]
        public async Task NoOtherActions_AreMapped(string verb, string url)
        {
            var request = new HttpRequestMessage(new HttpMethod(verb), url)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };

            var response = await _testClient.SendAsync(request);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        [Fact]
        public async Task GetArAging_ChangesNothing()
        {
            await GetAsync($"AsOfDate={AsOf}&CustomerId={_alphaId}");

            using var connection = _repositoryContext.GetDatabaseConnection();
            var statuses = (await connection.QueryAsync<string>(
                "SELECT status FROM ar_invoice WHERE number LIKE @Pattern ORDER BY number", new { Pattern = _marker + "%" })).ToArray();
            Assert.Equal(new[] { "unpaid", "partial", "unpaid", "paid", "unpaid" }, statuses);
        }
    }
}
