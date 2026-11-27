using System.Net;
using Newtonsoft.Json.Linq;

namespace Module.Tests.Controllers
{
    // GET /Report/GetRevenue (Story 26) against the test database. Seeded data is in March 2090:
    //   posted invoice 30 000 + fees 500 - discount 1 000 (tax 2 400, paid), interest received 625, other adjustment 100.
    // Excluded rows: draft and cancelled invoices, an AR receipt, a transfer, a statement-only line, a vendor refund.
    public class ReportControllerTest : TestBase, IAsyncLifetime
    {
        private readonly RevenueSeed _seed;

        public ReportControllerTest()
        {
            _seed = new RevenueSeed(() => _repositoryContext.GetDatabaseConnection());
        }

        public async Task InitializeAsync()
        {
            await _seed.InitializeAsync();
            var inMarch = new DateTime(2090, 3, 15);
            await _seed.AddInvoiceAsync(inMarch, 30000m, fees: 500m, discount: 1000m, tax: 2400m, paymentStatus: "paid");
            await _seed.AddInvoiceAsync(inMarch, 9999m, lifecycle: "draft");
            await _seed.AddInvoiceAsync(inMarch, 8888m, lifecycle: "cancelled");
            await _seed.AddAdjustmentAsync(inMarch, 625m, "Interest Received");
            await _seed.AddAdjustmentAsync(inMarch, 100m, "Other Adjustment");
            await _seed.AddAdjustmentAsync(inMarch, 777m, "Vendor Refund");
            await _seed.AddNonAdjustmentIncomingAsync(inMarch, 5000m, "BankReceipt");
            await _seed.AddNonAdjustmentIncomingAsync(inMarch, 4000m, "Transfer");
            await _seed.AddStatementOnlyAdjustmentAsync(inMarch, 3000m, "Interest Received");
        }

        public Task DisposeAsync() => _seed.CleanupAsync();

        private async Task<(HttpResponseMessage Response, JObject Json)> GetAsync(string query)
        {
            var response = await _testClient.GetAsync($"Report/GetRevenue?{query}");
            var text = await response.Content.ReadAsStringAsync();
            return (response, response.IsSuccessStatusCode ? JObject.Parse(text) : new JObject());
        }

        private static JToken Prop(JObject json, string name) =>
            json.GetValue(name, StringComparison.OrdinalIgnoreCase)!;

        // ---- 200, response shape ----

        [Fact]
        public async Task GetRevenue_Returns200WithTheDocumentedColumns()
        {
            var (response, json) = await GetAsync("Period_Type=Month&Anchor_Date=2090-03-15");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            foreach (var name in new[] { "period_Type", "start_Date", "end_Date", "vehicle_Revenue", "service_Revenue", "fee_Income",
                                         "interest_Income", "other_Income", "discounts", "total_Revenue", "invoice_Count" })
                Assert.NotNull(json.GetValue(name, StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task GetRevenue_Month_ReturnsThePeriodAndTheAmounts()
        {
            var (_, json) = await GetAsync("Period_Type=Month&Anchor_Date=2090-03-15");

            Assert.Equal("Month", Prop(json, "period_Type").ToString());
            Assert.Equal(new DateTime(2090, 3, 1), (DateTime)Prop(json, "start_Date"));
            Assert.Equal(new DateTime(2090, 3, 31), (DateTime)Prop(json, "end_Date"));
            Assert.Equal(30000m, (decimal)Prop(json, "vehicle_Revenue"));
            Assert.Equal(0m, (decimal)Prop(json, "service_Revenue"));
            Assert.Equal(500m, (decimal)Prop(json, "fee_Income"));
            Assert.Equal(625m, (decimal)Prop(json, "interest_Income"));
            Assert.Equal(100m, (decimal)Prop(json, "other_Income"));
            Assert.Equal(1000m, (decimal)Prop(json, "discounts"));
            Assert.Equal(1, (int)Prop(json, "invoice_Count"));
        }

        // ---- AC1-AC4 end to end ----

        [Fact]
        public async Task GetRevenue_TotalIsCalculated_ExcludingTaxReceiptsTransfersAndStatementOnlyLines()
        {
            var (_, json) = await GetAsync("Period_Type=Month&Anchor_Date=2090-03-15");

            // 30 000 + 0 + 500 + 625 + 100 - 1 000 = 30 225
            Assert.Equal(30225m, (decimal)Prop(json, "total_Revenue"));
        }

        [Fact]
        public async Task GetRevenue_PaidInvoice_IsStillCountedOnce()
        {
            var (_, json) = await GetAsync("Period_Type=Month&Anchor_Date=2090-03-15");

            Assert.Equal(1, (int)Prop(json, "invoice_Count"));
            Assert.Equal(30000m, (decimal)Prop(json, "vehicle_Revenue"));
        }

        [Fact]
        public async Task GetRevenue_QuarterAndYearToDate_IncludeTheSameData()
        {
            var (_, quarter) = await GetAsync("Period_Type=Quarter&Anchor_Date=2090-02-10");
            var (_, ytd) = await GetAsync("Period_Type=YearToDate&Anchor_Date=2090-03-20");

            Assert.Equal(new DateTime(2090, 1, 1), (DateTime)Prop(quarter, "start_Date"));
            Assert.Equal(new DateTime(2090, 3, 31), (DateTime)Prop(quarter, "end_Date"));
            Assert.Equal(30225m, (decimal)Prop(quarter, "total_Revenue"));
            Assert.Equal(new DateTime(2090, 3, 20), (DateTime)Prop(ytd, "end_Date"));
            Assert.Equal(30225m, (decimal)Prop(ytd, "total_Revenue"));
        }

        [Fact]
        public async Task GetRevenue_YearToDateEndingBeforeTheData_ExcludesIt()
        {
            var (_, json) = await GetAsync("Period_Type=YearToDate&Anchor_Date=2090-03-14");

            Assert.Equal(0m, (decimal)Prop(json, "total_Revenue"));
        }

        // ---- Empty period: 200 with zeros, not 404 ----

        [Fact]
        public async Task GetRevenue_PeriodWithNoRecords_Returns200WithAllAmountsZero()
        {
            var (response, json) = await GetAsync("Period_Type=Month&Anchor_Date=2090-04-15");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            foreach (var name in new[] { "vehicle_Revenue", "service_Revenue", "fee_Income", "interest_Income", "other_Income",
                                         "discounts", "total_Revenue" })
                Assert.Equal(0m, (decimal)Prop(json, name));
            Assert.Equal(0, (int)Prop(json, "invoice_Count"));
        }

        // ---- Invalid DTO -> 400 ----

        [Theory]
        [InlineData("Anchor_Date=2090-03-15")]
        [InlineData("Period_Type=Month")]
        [InlineData("")]
        [InlineData("Period_Type=Week&Anchor_Date=2090-03-15")]
        [InlineData("Period_Type=month&Anchor_Date=2090-03-15")]
        [InlineData("Period_Type=Month&Anchor_Date=not-a-date")]
        public async Task GetRevenue_InvalidPeriod_Returns400(string query)
        {
            var (response, _) = await GetAsync(query);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        // ---- Read-only: nothing is written or changed ----

        [Theory]
        [InlineData("POST")]
        [InlineData("PUT")]
        [InlineData("DELETE")]
        public async Task GetRevenue_WriteVerbs_AreNotAllowed(string method)
        {
            var request = new HttpRequestMessage(new HttpMethod(method), "Report/GetRevenue?Period_Type=Month&Anchor_Date=2090-03-15");

            var response = await _testClient.SendAsync(request);

            Assert.Contains(response.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
        }

        [Fact]
        public async Task OnlyGetRevenueIsMapped_OtherReportActionsReturn404()
        {
            var response = await _testClient.GetAsync("Report/GetProfitLoss?Period_Type=Month&Anchor_Date=2090-03-15");

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }
}
