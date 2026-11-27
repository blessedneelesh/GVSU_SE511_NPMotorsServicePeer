using System.Net;
using Newtonsoft.Json.Linq;

namespace Module.Tests.Controllers
{
    // GET /Dashboard/GetSummary against the test database. Seeded data, year 2095 (no other test uses it):
    //   February: invoice 25 000 on a vehicle costing 20 000
    //   March:    invoice 30 000 + fees 500 - discount 1 000 on a vehicle costing 25 200, interest received 625
    //             -> revenue 30 125, so the change from February is 20.5 %
    // Inventory, receivables and customers hold other data, so only their shape is checked here (the repository tests
    // check the numbers).
    public class DashboardControllerTest : TestBase, IAsyncLifetime
    {
        private readonly RevenueSeed _seed;

        public DashboardControllerTest()
        {
            _seed = new RevenueSeed(() => _repositoryContext.GetDatabaseConnection());
        }

        public async Task InitializeAsync()
        {
            await _seed.InitializeAsync();
            await SaleAsync(new DateTime(2095, 2, 10), 25000m, 20000m);
            await SaleAsync(new DateTime(2095, 3, 15), 30000m, 24000m, additionalCosts: 1200m, fees: 500m, discount: 1000m);
            await _seed.AddAdjustmentAsync(new DateTime(2095, 3, 15), 625m, "Interest Received", direction: "in");
        }

        public Task DisposeAsync() => _seed.CleanupAsync();

        private async Task SaleAsync(DateTime date, decimal price, decimal purchase, decimal additionalCosts = 0m, decimal fees = 0m, decimal discount = 0m)
        {
            var vehicle = await _seed.AddVehicleAsync(purchase, additionalCosts);
            await _seed.AddInvoiceAsync(date, price, fees: fees, discount: discount, vehicleId: vehicle);
        }

        private async Task<(HttpResponseMessage Response, JObject Json)> GetAsync(string query)
        {
            var response = await _testClient.GetAsync($"Dashboard/GetSummary{query}");
            var text = await response.Content.ReadAsStringAsync();
            return (response, response.IsSuccessStatusCode ? JObject.Parse(text) : new JObject());
        }

        private static JToken Prop(JToken json, string name) =>
            ((JObject)json).GetValue(name, StringComparison.OrdinalIgnoreCase)!;

        // ---- 200, response shape ----

        [Fact]
        public async Task GetSummary_Returns200WithEveryField()
        {
            var (response, json) = await GetAsync("?As_Of_Date=2095-03-15");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            foreach (var name in new[]
            {
                "as_Of_Date", "period_Start", "period_End", "total_Inventory", "available_Vehicles", "available_By_Make",
                "vehicles_Sold", "vehicles_Sold_Previous", "revenue", "revenue_Previous", "revenue_Change_Pct",
                "receivable_Balance", "open_Receivables", "active_Customers", "monthly_Revenue", "recent_Invoices"
            })
                Assert.True(json.ContainsKey(name), $"missing property {name}");
        }

        [Fact]
        public async Task GetSummary_PropertyNamesFollowTheBackendCasing()
        {
            var (_, json) = await GetAsync("?As_Of_Date=2095-03-15");

            Assert.NotNull(json.GetValue("as_Of_Date"));
            Assert.NotNull(json.GetValue("revenue_Change_Pct"));
            Assert.NotNull(json.GetValue("monthly_Revenue"));
        }

        [Fact]
        public async Task GetSummary_DatesAreWrittenAsPlainDates()
        {
            var (_, json) = await GetAsync("?As_Of_Date=2095-03-15");

            Assert.Equal("2095-03-15", Prop(json, "as_Of_Date").Value<string>());
            Assert.Equal("2095-03-01", Prop(json, "period_Start").Value<string>());
            Assert.Equal("2095-03-31", Prop(json, "period_End").Value<string>());
        }

        [Fact]
        public async Task GetSummary_CountsAreNonNegativeNumbers()
        {
            var (_, json) = await GetAsync("?As_Of_Date=2095-03-15");

            Assert.True(Prop(json, "total_Inventory").Value<int>() >= Prop(json, "available_Vehicles").Value<int>());
            Assert.True(Prop(json, "available_Vehicles").Value<int>() >= 0);
            Assert.True(Prop(json, "open_Receivables").Value<int>() >= 0);
            Assert.True(Prop(json, "active_Customers").Value<int>() >= 0);
            Assert.True(Prop(json, "vehicles_Sold").Value<int>() >= 1);
        }

        [Fact]
        public async Task GetSummary_AvailableByMake_ItemsHaveMakeAndCount_SumToAvailable()
        {
            var (_, json) = await GetAsync("?As_Of_Date=2095-03-15");

            var makes = (JArray)Prop(json, "available_By_Make");
            Assert.All(makes, m =>
            {
                Assert.False(string.IsNullOrWhiteSpace(Prop(m, "make").Value<string>()));
                Assert.True(Prop(m, "count").Value<int>() > 0);
            });
            // The seeded vehicles are sold or available Hondas; the grouped counts add up to the available total.
            Assert.Equal(Prop(json, "available_Vehicles").Value<int>(), makes.Sum(m => Prop(m, "count").Value<int>()));
        }

        // ---- Revenue and the Profit & Loss page agree ----

        [Fact]
        public async Task GetSummary_RevenueAndPrevious_AreTheMonthsFromTheSeed()
        {
            var (_, json) = await GetAsync("?As_Of_Date=2095-03-15");

            Assert.Equal(30125m, Prop(json, "revenue").Value<decimal>());
            Assert.Equal(25000m, Prop(json, "revenue_Previous").Value<decimal>());
            Assert.Equal(20.5m, Prop(json, "revenue_Change_Pct").Value<decimal>());
            Assert.Equal(1, Prop(json, "vehicles_Sold").Value<int>());
            Assert.Equal(1, Prop(json, "vehicles_Sold_Previous").Value<int>());
        }

        [Fact]
        public async Task GetSummary_PreviousRevenueZero_ChangeIsNull()
        {
            // February has revenue, January does not.
            var (_, json) = await GetAsync("?As_Of_Date=2095-02-15");

            Assert.Equal(25000m, Prop(json, "revenue").Value<decimal>());
            Assert.Equal(0m, Prop(json, "revenue_Previous").Value<decimal>());
            Assert.Equal(JTokenType.Null, Prop(json, "revenue_Change_Pct").Type);
        }

        [Fact]
        public async Task GetSummary_Revenue_EqualsReportGetRevenueAndComparePeriods()
        {
            var (_, json) = await GetAsync("?As_Of_Date=2095-03-15");
            var revenue = JObject.Parse(await _testClient.GetStringAsync("Report/GetRevenue?Period_Type=Month&Anchor_Date=2095-03-15"));
            var compare = JObject.Parse(await _testClient.GetStringAsync("Report/ComparePeriods?Period_Type=Month&Current_Anchor_Date=2095-03-15"));

            Assert.Equal(Prop(revenue, "total_Revenue").Value<decimal>(), Prop(json, "revenue").Value<decimal>());
            Assert.Equal(Prop(compare, "revenue_Change_Pct").Value<decimal?>(), Prop(json, "revenue_Change_Pct").Value<decimal?>());
            Assert.Equal(Prop(Prop(Prop(compare, "current"), "cogs"), "vehicles_Sold_Count").Value<int>(), Prop(json, "vehicles_Sold").Value<int>());
        }

        // ---- Monthly revenue ----

        [Fact]
        public async Task GetSummary_MonthlyRevenue_HasSevenMonthsOldestFirstEndingWithTheAsOfMonth()
        {
            var (_, json) = await GetAsync("?As_Of_Date=2095-03-15");

            var months = ((JArray)Prop(json, "monthly_Revenue")).ToList();
            Assert.Equal(new[] { "2094-09", "2094-10", "2094-11", "2094-12", "2095-01", "2095-02", "2095-03" },
                months.Select(m => Prop(m, "month").Value<string>()));
            Assert.Equal(25000m, Prop(months[5], "revenue").Value<decimal>());
            Assert.Equal(30125m, Prop(months[6], "revenue").Value<decimal>());
            Assert.Equal(0m, Prop(months[4], "revenue").Value<decimal>());
        }

        [Fact]
        public async Task GetSummary_MonthlyRevenue_EachMonthEqualsReportGetRevenue()
        {
            var (_, json) = await GetAsync("?As_Of_Date=2095-03-15");

            foreach (var month in (JArray)Prop(json, "monthly_Revenue"))
            {
                var label = Prop(month, "month").Value<string>()!;
                var report = JObject.Parse(await _testClient.GetStringAsync($"Report/GetRevenue?Period_Type=Month&Anchor_Date={label}-15"));
                Assert.Equal(Prop(report, "total_Revenue").Value<decimal>(), Prop(month, "revenue").Value<decimal>());
            }
        }

        // ---- Recent invoices ----

        [Fact]
        public async Task GetSummary_RecentInvoices_AreAtMostFourPostedNewestFirst()
        {
            var (_, json) = await GetAsync("?As_Of_Date=2095-03-15");

            var invoices = ((JArray)Prop(json, "recent_Invoices")).ToList();
            Assert.InRange(invoices.Count, 1, 4);
            Assert.All(invoices, i => Assert.Equal("posted", Prop(i, "lifecycle").Value<string>()));
            var dates = invoices.Select(i => Prop(i, "invoice_Date").Value<DateTime>()).ToList();
            Assert.Equal(dates.OrderByDescending(d => d), dates);
        }

        [Fact]
        public async Task GetSummary_RecentInvoices_UseTheSalesInvoiceItemShape()
        {
            var (_, json) = await GetAsync("?As_Of_Date=2095-03-15");

            var invoice = ((JArray)Prop(json, "recent_Invoices")).First();
            foreach (var name in new[] { "id", "number", "invoice_Date", "due_Date", "customer_Id", "customer_Name", "make", "model",
                                         "total", "paid", "balance", "lifecycle", "payment_Status" })
                Assert.True(((JObject)invoice).ContainsKey(name), $"missing property {name}");
            Assert.Equal(Prop(invoice, "total").Value<decimal>() - Prop(invoice, "paid").Value<decimal>(), Prop(invoice, "balance").Value<decimal>());
        }

        // ---- As_Of_Date ----

        [Fact]
        public async Task GetSummary_NoAsOfDate_DefaultsToToday()
        {
            var (response, json) = await GetAsync(string.Empty);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var today = DateTime.Today;
            Assert.Equal(today.ToString("yyyy-MM-dd"), Prop(json, "as_Of_Date").Value<string>());
            Assert.Equal(new DateTime(today.Year, today.Month, 1).ToString("yyyy-MM-dd"), Prop(json, "period_Start").Value<string>());
            Assert.Equal(7, ((JArray)Prop(json, "monthly_Revenue")).Count);
        }

        [Theory]
        [InlineData("not-a-date")]
        [InlineData("2095-13-01")]
        [InlineData("2095-02-30")]
        public async Task GetSummary_InvalidAsOfDate_Returns400WithProblemDetails(string value)
        {
            var response = await _testClient.GetAsync($"Dashboard/GetSummary?As_Of_Date={value}");

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var body = JObject.Parse(await response.Content.ReadAsStringAsync());
            Assert.NotNull(body.GetValue("status"));
            Assert.Equal(400, body.Value<int>("status"));
            Assert.NotNull(body.GetValue("errors"));
        }

        // ---- Only the drawn action exists ----

        [Theory]
        [InlineData("POST")]
        [InlineData("PUT")]
        [InlineData("DELETE")]
        public async Task GetSummary_WriteMethods_AreNotAllowed(string method)
        {
            var response = await _testClient.SendAsync(new HttpRequestMessage(new HttpMethod(method), "Dashboard/GetSummary"));

            Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed);
        }
    }
}
