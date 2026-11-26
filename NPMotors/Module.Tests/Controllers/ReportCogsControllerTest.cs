using System.Net;
using Newtonsoft.Json.Linq;

namespace Module.Tests.Controllers
{
    // GET /Report/GetCostOfGoodsSold (Story 27) against the test database. Seeded data is in March 2091:
    //   sold on posted invoices: vehicle A (cost 25 200, paid invoice), vehicle B (cost 10 000, unpaid invoice).
    // Excluded: an unsold vehicle with a paid AP bill, a vehicle on a draft invoice, a vehicle on a cancelled invoice.
    public class ReportCogsControllerTest : TestBase, IAsyncLifetime
    {
        private readonly RevenueSeed _seed;

        public ReportCogsControllerTest()
        {
            _seed = new RevenueSeed(() => _repositoryContext.GetDatabaseConnection());
        }

        public async Task InitializeAsync()
        {
            await _seed.InitializeAsync();
            var inMarch = new DateTime(2091, 3, 15);
            var sold = await _seed.AddVehicleAsync(24000m, 1200m);
            var soldUnpaid = await _seed.AddVehicleAsync(10000m);
            var draft = await _seed.AddVehicleAsync(7777m);
            var cancelled = await _seed.AddVehicleAsync(8888m);
            await _seed.AddVehicleAsync(9999m);
            await _seed.AddApBillAsync(9999m, paid: 9999m);
            await _seed.AddInvoiceAsync(inMarch, 30000m, paymentStatus: "paid", vehicleId: sold);
            await _seed.AddInvoiceAsync(inMarch, 12000m, vehicleId: soldUnpaid);
            await _seed.AddInvoiceAsync(inMarch, 9000m, lifecycle: "draft", vehicleId: draft);
            await _seed.AddInvoiceAsync(inMarch, 9500m, lifecycle: "cancelled", vehicleId: cancelled);
        }

        public Task DisposeAsync() => _seed.CleanupAsync();

        private async Task<(HttpResponseMessage Response, JObject Json)> GetAsync(string query)
        {
            var response = await _testClient.GetAsync($"Report/GetCostOfGoodsSold?{query}");
            var text = await response.Content.ReadAsStringAsync();
            return (response, response.IsSuccessStatusCode ? JObject.Parse(text) : new JObject());
        }

        private static JToken Prop(JObject json, string name) =>
            json.GetValue(name, StringComparison.OrdinalIgnoreCase)!;

        // ---- 200, response shape ----

        [Fact]
        public async Task GetCostOfGoodsSold_Returns200WithTheDocumentedColumns()
        {
            var (response, json) = await GetAsync("Period_Type=Month&Anchor_Date=2091-03-15");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            foreach (var name in new[] { "period_Type", "start_Date", "end_Date", "vehicle_Cogs", "parts_Cogs", "total_Cogs", "vehicles_Sold_Count" })
                Assert.NotNull(json.GetValue(name, StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task GetCostOfGoodsSold_Month_ReturnsThePeriodAndTheAmounts()
        {
            var (_, json) = await GetAsync("Period_Type=Month&Anchor_Date=2091-03-15");

            Assert.Equal("Month", Prop(json, "period_Type").ToString());
            Assert.Equal(new DateTime(2091, 3, 1), (DateTime)Prop(json, "start_Date"));
            Assert.Equal(new DateTime(2091, 3, 31), (DateTime)Prop(json, "end_Date"));
            Assert.Equal(35200m, (decimal)Prop(json, "vehicle_Cogs"));
            Assert.Equal(0m, (decimal)Prop(json, "parts_Cogs"));
            Assert.Equal(35200m, (decimal)Prop(json, "total_Cogs"));
            Assert.Equal(2, (int)Prop(json, "vehicles_Sold_Count"));
        }

        // ---- AC1-AC4 end to end ----

        [Fact]
        public async Task GetCostOfGoodsSold_CountsOnlyPostedSales_NotUnsoldDraftOrCancelled()
        {
            var (_, json) = await GetAsync("Period_Type=Month&Anchor_Date=2091-03-15");

            // 25 200 + 10 000; the 9 999 unsold vehicle, 7 777 draft and 8 888 cancelled vehicles add nothing.
            Assert.Equal(35200m, (decimal)Prop(json, "vehicle_Cogs"));
            Assert.Equal(2, (int)Prop(json, "vehicles_Sold_Count"));
        }

        [Fact]
        public async Task GetCostOfGoodsSold_QuarterAndYearToDate_IncludeTheSameData()
        {
            var (_, quarter) = await GetAsync("Period_Type=Quarter&Anchor_Date=2091-02-10");
            var (_, ytd) = await GetAsync("Period_Type=YearToDate&Anchor_Date=2091-03-20");

            Assert.Equal(new DateTime(2091, 1, 1), (DateTime)Prop(quarter, "start_Date"));
            Assert.Equal(new DateTime(2091, 3, 31), (DateTime)Prop(quarter, "end_Date"));
            Assert.Equal(35200m, (decimal)Prop(quarter, "total_Cogs"));
            Assert.Equal(new DateTime(2091, 3, 20), (DateTime)Prop(ytd, "end_Date"));
            Assert.Equal(35200m, (decimal)Prop(ytd, "total_Cogs"));
        }

        [Fact]
        public async Task GetCostOfGoodsSold_YearToDateEndingBeforeTheSales_ExcludesThem()
        {
            var (_, json) = await GetAsync("Period_Type=YearToDate&Anchor_Date=2091-03-14");

            Assert.Equal(0m, (decimal)Prop(json, "total_Cogs"));
        }

        // ---- Empty period: 200 with zeros, not 404 ----

        [Fact]
        public async Task GetCostOfGoodsSold_PeriodWithNoSales_Returns200WithAllAmountsZero()
        {
            var (response, json) = await GetAsync("Period_Type=Month&Anchor_Date=2091-04-15");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(0m, (decimal)Prop(json, "vehicle_Cogs"));
            Assert.Equal(0m, (decimal)Prop(json, "parts_Cogs"));
            Assert.Equal(0m, (decimal)Prop(json, "total_Cogs"));
            Assert.Equal(0, (int)Prop(json, "vehicles_Sold_Count"));
        }

        // ---- Invalid DTO -> 400 ----

        [Theory]
        [InlineData("Anchor_Date=2091-03-15")]
        [InlineData("Period_Type=Month")]
        [InlineData("")]
        [InlineData("Period_Type=Week&Anchor_Date=2091-03-15")]
        [InlineData("Period_Type=month&Anchor_Date=2091-03-15")]
        [InlineData("Period_Type=Month&Anchor_Date=not-a-date")]
        public async Task GetCostOfGoodsSold_InvalidPeriod_Returns400(string query)
        {
            var (response, _) = await GetAsync(query);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        // ---- Read-only ----

        [Theory]
        [InlineData("POST")]
        [InlineData("PUT")]
        [InlineData("DELETE")]
        public async Task GetCostOfGoodsSold_WriteVerbs_AreNotAllowed(string method)
        {
            var request = new HttpRequestMessage(new HttpMethod(method), "Report/GetCostOfGoodsSold?Period_Type=Month&Anchor_Date=2091-03-15");

            var response = await _testClient.SendAsync(request);

            Assert.Contains(response.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
        }

        [Fact]
        public async Task GetRevenue_StillWorksAlongsideTheNewAction()
        {
            var response = await _testClient.GetAsync("Report/GetRevenue?Period_Type=Month&Anchor_Date=2091-03-15");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }
}
