using System.Net;
using Newtonsoft.Json.Linq;

namespace Module.Tests.Controllers
{
    // GET /Report/ComparePeriods (Story 30) against the test database. Seeded data, 2094 (every sale has its own vehicle):
    //   February: invoice 25 000 on a vehicle costing 20 000                      -> revenue 25 000, COGS 20 000, gross 5 000 (20.00 %)
    //   March:    invoice 30 000 + fees 500 - discount 1 000 on a vehicle costing 25 200, interest received 625
    //                                                                              -> revenue 30 125, COGS 25 200, gross 4 925 (16.35 %)
    //   April:    invoice 10 000 on a vehicle costing 4 000                         -> gross 6 000 (60.00 %)
    //   May:      invoice 6 000 on a vehicle costing 9 000                          -> gross -3 000 (-50.00 %), a loss
    // Payroll comes from the shared employee table, so only the changes that do not depend on it are checked exactly.
    public class ReportComparePeriodsControllerTest : TestBase, IAsyncLifetime
    {
        private readonly RevenueSeed _seed;

        public ReportComparePeriodsControllerTest()
        {
            _seed = new RevenueSeed(() => _repositoryContext.GetDatabaseConnection());
        }

        public async Task InitializeAsync()
        {
            await _seed.InitializeAsync();
            await SaleAsync(new DateTime(2094, 2, 10), 25000m, 20000m);
            await SaleAsync(new DateTime(2094, 3, 15), 30000m, 24000m, additionalCosts: 1200m, fees: 500m, discount: 1000m);
            await _seed.AddAdjustmentAsync(new DateTime(2094, 3, 15), 625m, "Interest Received", direction: "in");
            await SaleAsync(new DateTime(2094, 4, 12), 10000m, 4000m);
            await SaleAsync(new DateTime(2094, 5, 12), 6000m, 9000m);
        }

        public Task DisposeAsync() => _seed.CleanupAsync();

        private async Task SaleAsync(DateTime date, decimal price, decimal purchase, decimal additionalCosts = 0m, decimal fees = 0m, decimal discount = 0m)
        {
            var vehicle = await _seed.AddVehicleAsync(purchase, additionalCosts);
            await _seed.AddInvoiceAsync(date, price, fees: fees, discount: discount, vehicleId: vehicle);
        }

        private async Task<(HttpResponseMessage Response, JObject Json)> GetAsync(string query)
        {
            var response = await _testClient.GetAsync($"Report/ComparePeriods?{query}");
            var text = await response.Content.ReadAsStringAsync();
            return (response, response.IsSuccessStatusCode ? JObject.Parse(text) : new JObject());
        }

        private static JToken Prop(JToken json, string name) =>
            ((JObject)json).GetValue(name, StringComparison.OrdinalIgnoreCase)!;

        // ---- 200, response shape ----

        [Fact]
        public async Task ComparePeriods_Returns200WithBothReportsAndTheChangeColumns()
        {
            var (response, json) = await GetAsync("Period_Type=Month&Current_Anchor_Date=2094-03-15");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            foreach (var name in new[] { "current", "previous", "revenue_Change_Pct", "cogs_Change_Pct", "gross_Profit_Change_Pct",
                                         "operating_Expense_Change_Pct", "net_Profit_Change_Pct", "gross_Margin_Change", "net_Margin_Change" })
                Assert.NotNull(json.GetValue(name, StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(Prop(Prop(json, "current"), "total_Revenue"));
            Assert.NotNull(Prop(Prop(json, "previous"), "net_Profit"));
        }

        // ---- Growth, decline and margin change (default previous period) ----

        [Fact]
        public async Task ComparePeriods_MonthVersusThePreviousMonth_ReturnsBothPeriods()
        {
            var (_, json) = await GetAsync("Period_Type=Month&Current_Anchor_Date=2094-03-15");

            var current = Prop(json, "current");
            var previous = Prop(json, "previous");
            Assert.Equal(new DateTime(2094, 3, 1), (DateTime)Prop(current, "start_Date"));
            Assert.Equal(new DateTime(2094, 3, 31), (DateTime)Prop(current, "end_Date"));
            Assert.Equal(new DateTime(2094, 2, 1), (DateTime)Prop(previous, "start_Date"));
            Assert.Equal(new DateTime(2094, 2, 28), (DateTime)Prop(previous, "end_Date"));
            Assert.Equal(30125m, (decimal)Prop(current, "total_Revenue"));
            Assert.Equal(25000m, (decimal)Prop(previous, "total_Revenue"));
        }

        [Fact]
        public async Task ComparePeriods_MonthVersusThePreviousMonth_ReturnsTheChangesThatDoNotDependOnPayroll()
        {
            var (_, json) = await GetAsync("Period_Type=Month&Current_Anchor_Date=2094-03-15");

            Assert.Equal(20.50m, (decimal)Prop(json, "revenue_Change_Pct"));
            Assert.Equal(26.00m, (decimal)Prop(json, "cogs_Change_Pct"));
            Assert.Equal(-1.50m, (decimal)Prop(json, "gross_Profit_Change_Pct"));
            Assert.Equal(-3.65m, (decimal)Prop(json, "gross_Margin_Change"));
        }

        [Fact]
        public async Task ComparePeriods_ExplicitPreviousAnchor_IsUsed()
        {
            var (_, json) = await GetAsync("Period_Type=Month&Current_Anchor_Date=2094-04-20&Previous_Anchor_Date=2094-02-10");

            Assert.Equal(new DateTime(2094, 2, 1), (DateTime)Prop(Prop(json, "previous"), "start_Date"));
            // Revenue 10 000 against 25 000.
            Assert.Equal(-60.00m, (decimal)Prop(json, "revenue_Change_Pct"));
        }

        // ---- AC3: negative profit displays clearly ----

        [Fact]
        public async Task ComparePeriods_ProfitTurningIntoALoss_ShowsTheNegativeProfitAndTheNegativeChange()
        {
            var (_, json) = await GetAsync("Period_Type=Month&Current_Anchor_Date=2094-05-15");

            Assert.Equal(-3000m, (decimal)Prop(Prop(json, "current"), "gross_Profit"));
            Assert.Equal(-50.00m, (decimal)Prop(Prop(json, "current"), "gross_Margin"));
            Assert.Equal(6000m, (decimal)Prop(Prop(json, "previous"), "gross_Profit"));
            // (-3 000 - 6 000) / 6 000 x 100
            Assert.Equal(-150.00m, (decimal)Prop(json, "gross_Profit_Change_Pct"));
            // -50.00 % - 60.00 % = -110.00 points
            Assert.Equal(-110.00m, (decimal)Prop(json, "gross_Margin_Change"));
            Assert.Equal(-40.00m, (decimal)Prop(json, "revenue_Change_Pct"));
            Assert.Equal(125.00m, (decimal)Prop(json, "cogs_Change_Pct"));
        }

        [Fact]
        public async Task ComparePeriods_LossComparedWithAnEarlierNonAdjacentProfitableMonth_ShowsANegativeChange()
        {
            // Current May (gross loss 3 000) against an explicit earlier March (gross profit 4 925).
            var (_, json) = await GetAsync("Period_Type=Month&Current_Anchor_Date=2094-05-15&Previous_Anchor_Date=2094-03-15");

            // Gross profit 4 925 -> -3 000: (-3 000 - 4 925) / 4 925 x 100 = -160.91
            Assert.Equal(-160.91m, (decimal)Prop(json, "gross_Profit_Change_Pct"));
        }

        // ---- AC2: unavailable when the previous amount is zero ----

        [Fact]
        public async Task ComparePeriods_PreviousPeriodWithNoSales_ReturnsNullChangesForRevenueCogsAndProfit()
        {
            var (response, json) = await GetAsync("Period_Type=Month&Current_Anchor_Date=2094-02-15");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(JTokenType.Null, Prop(json, "revenue_Change_Pct").Type);
            Assert.Equal(JTokenType.Null, Prop(json, "cogs_Change_Pct").Type);
            Assert.Equal(JTokenType.Null, Prop(json, "gross_Profit_Change_Pct").Type);
            Assert.Equal(JTokenType.Null, Prop(json, "gross_Margin_Change").Type);
            Assert.Equal(JTokenType.Null, Prop(json, "net_Margin_Change").Type);
        }

        // ---- Quarter and year to date ----

        [Fact]
        public async Task ComparePeriods_QuarterVersusThePreviousQuarter_ComparesCalendarQuarters()
        {
            var (_, json) = await GetAsync("Period_Type=Quarter&Current_Anchor_Date=2094-06-15");

            var current = Prop(json, "current");
            var previous = Prop(json, "previous");
            Assert.Equal(new DateTime(2094, 4, 1), (DateTime)Prop(current, "start_Date"));
            Assert.Equal(new DateTime(2094, 6, 30), (DateTime)Prop(current, "end_Date"));
            Assert.Equal(new DateTime(2094, 1, 1), (DateTime)Prop(previous, "start_Date"));
            Assert.Equal(new DateTime(2094, 3, 31), (DateTime)Prop(previous, "end_Date"));
            // Current: 10 000 + 6 000 = 16 000; previous: 25 000 + 30 125 = 55 125.
            Assert.Equal(16000m, (decimal)Prop(current, "total_Revenue"));
            Assert.Equal(55125m, (decimal)Prop(previous, "total_Revenue"));
            Assert.Equal(-70.98m, (decimal)Prop(json, "revenue_Change_Pct"));
        }

        [Fact]
        public async Task ComparePeriods_YearToDateVersusThePreviousYear_ReturnsBothPeriodsAndUnavailableChange()
        {
            var (_, json) = await GetAsync("Period_Type=YearToDate&Current_Anchor_Date=2094-03-20");

            Assert.Equal(new DateTime(2094, 1, 1), (DateTime)Prop(Prop(json, "current"), "start_Date"));
            Assert.Equal(new DateTime(2094, 3, 20), (DateTime)Prop(Prop(json, "current"), "end_Date"));
            Assert.Equal(new DateTime(2093, 1, 1), (DateTime)Prop(Prop(json, "previous"), "start_Date"));
            Assert.Equal(new DateTime(2093, 3, 20), (DateTime)Prop(Prop(json, "previous"), "end_Date"));
            Assert.Equal(55125m, (decimal)Prop(Prop(json, "current"), "total_Revenue"));
            // 2093 has no sales in this seed, but other tests seed 2093 data, so the change itself is not asserted.
        }

        // ---- AC1: periods must not overlap -> 400 ----

        [Theory]
        [InlineData("Period_Type=Month&Current_Anchor_Date=2094-03-15&Previous_Anchor_Date=2094-03-05")]
        [InlineData("Period_Type=Month&Current_Anchor_Date=2094-03-15&Previous_Anchor_Date=2094-03-01")]
        [InlineData("Period_Type=Month&Current_Anchor_Date=2094-03-15&Previous_Anchor_Date=2094-04-10")]
        [InlineData("Period_Type=Quarter&Current_Anchor_Date=2094-05-20&Previous_Anchor_Date=2094-06-30")]
        [InlineData("Period_Type=YearToDate&Current_Anchor_Date=2094-03-15&Previous_Anchor_Date=2094-01-01")]
        public async Task ComparePeriods_OverlappingPeriods_Returns400(string query)
        {
            var (response, _) = await GetAsync(query);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task ComparePeriods_PreviousPeriodEndingTheDayBeforeTheCurrentOne_Returns200()
        {
            var (response, _) = await GetAsync("Period_Type=Month&Current_Anchor_Date=2094-03-15&Previous_Anchor_Date=2094-02-28");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // ---- Invalid DTO -> 400 ----

        [Theory]
        [InlineData("Current_Anchor_Date=2094-03-15")]
        [InlineData("Period_Type=Month")]
        [InlineData("")]
        [InlineData("Period_Type=Week&Current_Anchor_Date=2094-03-15")]
        [InlineData("Period_Type=month&Current_Anchor_Date=2094-03-15")]
        [InlineData("Period_Type=Month&Current_Anchor_Date=not-a-date")]
        [InlineData("Period_Type=Month&Current_Anchor_Date=2094-03-15&Previous_Anchor_Date=not-a-date")]
        public async Task ComparePeriods_InvalidRequest_Returns400(string query)
        {
            var (response, _) = await GetAsync(query);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        // ---- Read-only ----

        [Theory]
        [InlineData("POST")]
        [InlineData("PUT")]
        [InlineData("DELETE")]
        public async Task ComparePeriods_WriteVerbs_AreNotAllowed(string method)
        {
            var request = new HttpRequestMessage(new HttpMethod(method),
                "Report/ComparePeriods?Period_Type=Month&Current_Anchor_Date=2094-03-15");

            var response = await _testClient.SendAsync(request);

            Assert.Contains(response.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
        }
    }
}
