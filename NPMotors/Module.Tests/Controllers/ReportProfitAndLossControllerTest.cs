using System.Net;
using Newtonsoft.Json.Linq;

namespace Module.Tests.Controllers
{
    // GET /Report/GetProfitAndLoss (Story 29) against the test database. Seeded data, March 2093:
    //   posted invoice: price 30 000, fees 500, discount 1 000, tax 2 400, on a vehicle costing 25 200; interest received 625
    //   -> revenue 30 125, COGS 25 200, gross profit 4 925 (16.35 %);
    //   excluded: a draft invoice and a cancelled invoice (each on their own vehicle), an unsold vehicle;
    //   expenses: Office Expense payment 500 and Bank Service Charge 125 (payroll payment 38 450 excluded).
    // Payroll comes from the shared employee table, so only figures that do not depend on it are checked exactly;
    // net figures are checked through their relation to the gross figures and the operating expenses.
    public class ReportProfitAndLossControllerTest : TestBase, IAsyncLifetime
    {
        private const string MonthQuery = "Period_Type=Month&Anchor_Date=2093-03-15";
        private const string EmptyMonthQuery = "Period_Type=Month&Anchor_Date=2093-04-15";

        private readonly RevenueSeed _seed;

        public ReportProfitAndLossControllerTest()
        {
            _seed = new RevenueSeed(() => _repositoryContext.GetDatabaseConnection());
        }

        public async Task InitializeAsync()
        {
            await _seed.InitializeAsync();
            var inMarch = new DateTime(2093, 3, 15);
            var sold = await _seed.AddVehicleAsync(24000m, 1200m);
            var draftVehicle = await _seed.AddVehicleAsync(7777m);
            var cancelledVehicle = await _seed.AddVehicleAsync(8888m);
            await _seed.AddVehicleAsync(9999m);
            await _seed.AddInvoiceAsync(inMarch, 30000m, fees: 500m, discount: 1000m, tax: 2400m, paymentStatus: "paid", vehicleId: sold);
            await _seed.AddInvoiceAsync(inMarch, 9000m, lifecycle: "draft", vehicleId: draftVehicle);
            await _seed.AddInvoiceAsync(inMarch, 9500m, lifecycle: "cancelled", vehicleId: cancelledVehicle);
            await _seed.AddAdjustmentAsync(inMarch, 625m, "Interest Received", direction: "in");

            await _seed.AddPaymentAsync(inMarch, 500m, "Office Expense");
            await _seed.AddAdjustmentAsync(inMarch, 125m, "Bank Service Charge", direction: "out");
            await _seed.AddPaymentAsync(inMarch, 38450m, "Payroll");
        }

        public Task DisposeAsync() => _seed.CleanupAsync();

        private async Task<(HttpResponseMessage Response, JObject Json)> GetAsync(string query)
        {
            var response = await _testClient.GetAsync($"Report/GetProfitAndLoss?{query}");
            var text = await response.Content.ReadAsStringAsync();
            return (response, response.IsSuccessStatusCode ? JObject.Parse(text) : new JObject());
        }

        private static JToken Prop(JToken json, string name) =>
            ((JObject)json).GetValue(name, StringComparison.OrdinalIgnoreCase)!;

        // Rounded to cents: JSON parsing cannot hold the long decimal fractions payroll can produce.
        private static decimal Cents(JToken json, string name) => Math.Round((decimal)Prop(json, name), 2);

        // ---- 200, response shape (AC1) ----

        [Fact]
        public async Task GetProfitAndLoss_Returns200WithTheDocumentedColumns()
        {
            var (response, json) = await GetAsync(MonthQuery);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            foreach (var name in new[] { "period_Type", "start_Date", "end_Date", "revenue", "cogs", "operating_Expenses", "total_Revenue",
                                         "total_Cogs", "gross_Profit", "total_Operating_Expenses", "net_Profit", "gross_Margin",
                                         "net_Margin", "generated_Date", "generated_By" })
                Assert.NotNull(json.GetValue(name, StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task GetProfitAndLoss_Month_ReturnsThePeriod()
        {
            var (_, json) = await GetAsync(MonthQuery);

            Assert.Equal("Month", Prop(json, "period_Type").ToString());
            Assert.Equal(new DateTime(2093, 3, 1), (DateTime)Prop(json, "start_Date"));
            Assert.Equal(new DateTime(2093, 3, 31), (DateTime)Prop(json, "end_Date"));
        }

        [Fact]
        public async Task GetProfitAndLoss_NestedSummariesCarryTheirOwnPeriodAndAmounts()
        {
            var (_, json) = await GetAsync(MonthQuery);

            Assert.Equal(30000m, (decimal)Prop(Prop(json, "revenue"), "vehicle_Revenue"));
            Assert.Equal(25200m, (decimal)Prop(Prop(json, "cogs"), "vehicle_Cogs"));
            Assert.Equal(1, (int)Prop(Prop(json, "cogs"), "vehicles_Sold_Count"));
            Assert.Equal(500m, (decimal)Prop(Prop(json, "operating_Expenses"), "categorized_Payments"));
            Assert.Equal(125m, (decimal)Prop(Prop(json, "operating_Expenses"), "bank_Charges"));
        }

        // ---- AC1: figures ----

        [Fact]
        public async Task GetProfitAndLoss_ShowsRevenueCogsGrossProfitAndGrossMargin()
        {
            var (_, json) = await GetAsync(MonthQuery);

            Assert.Equal(30125m, (decimal)Prop(json, "total_Revenue"));
            Assert.Equal(25200m, (decimal)Prop(json, "total_Cogs"));
            Assert.Equal(4925m, (decimal)Prop(json, "gross_Profit"));
            Assert.Equal(16.35m, (decimal)Prop(json, "gross_Margin"));
        }

        [Fact]
        public async Task GetProfitAndLoss_TotalsMatchTheNestedSummaries()
        {
            var (_, json) = await GetAsync(MonthQuery);

            Assert.Equal(Cents(Prop(json, "revenue"), "total_Revenue"), Cents(json, "total_Revenue"));
            Assert.Equal(Cents(Prop(json, "cogs"), "total_Cogs"), Cents(json, "total_Cogs"));
            Assert.Equal(Cents(Prop(json, "operating_Expenses"), "total_Operating_Expenses"), Cents(json, "total_Operating_Expenses"));
        }

        [Fact]
        public async Task GetProfitAndLoss_NetProfitIsGrossProfitMinusOperatingExpenses()
        {
            var (_, json) = await GetAsync(MonthQuery);

            Assert.Equal(Cents(json, "gross_Profit") - Cents(json, "total_Operating_Expenses"), Cents(json, "net_Profit"), 1);
        }

        [Fact]
        public async Task GetProfitAndLoss_OperatingExpensesIncludeThePaymentAndTheBankChargeButNotPayroll()
        {
            var (_, json) = await GetAsync(MonthQuery);

            var expenses = Prop(json, "operating_Expenses");
            Assert.Equal(625m, Cents(expenses, "categorized_Payments") + Cents(expenses, "bank_Charges"));
        }

        [Fact]
        public async Task GetProfitAndLoss_NetMarginIsNetProfitOverRevenue()
        {
            var (_, json) = await GetAsync(MonthQuery);

            var expected = Cents(json, "net_Profit") / 30125m * 100m;
            Assert.Equal((double)expected, (double)(decimal)Prop(json, "net_Margin"), 1);
        }

        // ---- AC2: draft and cancelled invoices are excluded ----

        [Fact]
        public async Task GetProfitAndLoss_DraftAndCancelledInvoices_AreNotInRevenueOrCogs()
        {
            var (_, json) = await GetAsync(MonthQuery);

            // The 9 000 draft, 9 500 cancelled and their vehicle costs (7 777, 8 888) would change these figures.
            Assert.Equal(30000m, (decimal)Prop(Prop(json, "revenue"), "vehicle_Revenue"));
            Assert.Equal(1, (int)Prop(Prop(json, "revenue"), "invoice_Count"));
            Assert.Equal(25200m, (decimal)Prop(json, "total_Cogs"));
        }

        // ---- AC3: same result for the same period and data ----

        [Fact]
        public async Task GetProfitAndLoss_CalledTwice_GivesTheSameFiguresAndOnlyGeneratedDateMayDiffer()
        {
            var (_, first) = await GetAsync(MonthQuery);
            var (_, second) = await GetAsync(MonthQuery);

            // Payroll figures (operating expenses, net profit, net margin) are left out: other tests running in parallel add
            // salaried employees to the shared employee table between the two calls. The service test checks every figure.
            foreach (var name in new[] { "total_Revenue", "total_Cogs", "gross_Profit", "gross_Margin" })
                Assert.Equal(Prop(first, name).ToString(), Prop(second, name).ToString());
            foreach (var name in new[] { "categorized_Payments", "bank_Charges" })
                Assert.Equal(Prop(Prop(first, "operating_Expenses"), name).ToString(), Prop(Prop(second, "operating_Expenses"), name).ToString());
            Assert.Equal(Prop(first, "start_Date").ToString(), Prop(second, "start_Date").ToString());
            Assert.Equal(Prop(first, "end_Date").ToString(), Prop(second, "end_Date").ToString());
        }

        [Fact]
        public async Task GetProfitAndLoss_GeneratedDateIsRecentAndGeneratedByIsEmptyWithoutASignedInUser()
        {
            var before = DateTime.Now.AddMinutes(-1);

            var (_, json) = await GetAsync(MonthQuery);

            Assert.InRange((DateTime)Prop(json, "generated_Date"), before, DateTime.Now.AddMinutes(1));
            Assert.Equal(string.Empty, Prop(json, "generated_By").ToString());
        }

        // ---- Period with no records: 200, not 404 ----

        [Fact]
        public async Task GetProfitAndLoss_PeriodWithNoRecords_Returns200WithZeroRevenueAndNullMargins()
        {
            var (response, json) = await GetAsync(EmptyMonthQuery);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(0m, (decimal)Prop(json, "total_Revenue"));
            Assert.Equal(0m, (decimal)Prop(json, "total_Cogs"));
            Assert.Equal(0m, (decimal)Prop(json, "gross_Profit"));
            Assert.Equal(JTokenType.Null, Prop(json, "gross_Margin").Type);
            Assert.Equal(JTokenType.Null, Prop(json, "net_Margin").Type);
        }

        // ---- Quarter and year to date ----

        [Fact]
        public async Task GetProfitAndLoss_QuarterAndYearToDate_IncludeTheSameSales()
        {
            var (_, quarter) = await GetAsync("Period_Type=Quarter&Anchor_Date=2093-02-10");
            var (_, ytd) = await GetAsync("Period_Type=YearToDate&Anchor_Date=2093-03-20");

            Assert.Equal(new DateTime(2093, 1, 1), (DateTime)Prop(quarter, "start_Date"));
            Assert.Equal(new DateTime(2093, 3, 31), (DateTime)Prop(quarter, "end_Date"));
            Assert.Equal(30125m, (decimal)Prop(quarter, "total_Revenue"));
            Assert.Equal(4925m, (decimal)Prop(quarter, "gross_Profit"));
            Assert.Equal(new DateTime(2093, 3, 20), (DateTime)Prop(ytd, "end_Date"));
            Assert.Equal(30125m, (decimal)Prop(ytd, "total_Revenue"));
        }

        // ---- Invalid DTO -> 400 ----

        [Theory]
        [InlineData("Anchor_Date=2093-03-15")]
        [InlineData("Period_Type=Month")]
        [InlineData("")]
        [InlineData("Period_Type=Week&Anchor_Date=2093-03-15")]
        [InlineData("Period_Type=month&Anchor_Date=2093-03-15")]
        [InlineData("Period_Type=Month&Anchor_Date=not-a-date")]
        public async Task GetProfitAndLoss_InvalidPeriod_Returns400(string query)
        {
            var (response, _) = await GetAsync(query);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        // ---- Read-only ----

        [Theory]
        [InlineData("POST")]
        [InlineData("PUT")]
        [InlineData("DELETE")]
        public async Task GetProfitAndLoss_WriteVerbs_AreNotAllowed(string method)
        {
            var request = new HttpRequestMessage(new HttpMethod(method), $"Report/GetProfitAndLoss?{MonthQuery}");

            var response = await _testClient.SendAsync(request);

            Assert.Contains(response.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
        }
    }
}
