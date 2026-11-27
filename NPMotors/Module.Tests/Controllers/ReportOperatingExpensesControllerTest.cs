using System.Net;
using Newtonsoft.Json.Linq;

namespace Module.Tests.Controllers
{
    // GET /Report/GetOperatingExpenses (Story 28) against the test database. Seeded data, March 2092:
    //   one active employee: salary 120 000, bonus 24 000, commission 10 %, one posted invoice of 1 000;
    //   counted: Office Expense payment 500, Bank Service Charge 125, Card Processing Fee 40;
    //   excluded: payroll, tax, loan, refund and transfer payments, an AP-bill payment, a vehicle payment, a reversed payment,
    //             an outgoing Interest Received adjustment, an incoming interest adjustment, a statement-only charge.
    // The employee table is shared, so payroll figures are checked as the difference from a baseline taken before seeding.
    public class ReportOperatingExpensesControllerTest : TestBase, IAsyncLifetime
    {
        private const string MonthQuery = "Period_Type=Month&Anchor_Date=2092-03-15";
        private const string QuarterQuery = "Period_Type=Quarter&Anchor_Date=2092-02-10";
        private const string YtdQuery = "Period_Type=YearToDate&Anchor_Date=2092-03-20";
        private const string EmptyMonthQuery = "Period_Type=Month&Anchor_Date=2092-04-15";

        private readonly RevenueSeed _seed;
        private readonly Dictionary<string, JObject> _baseline = new();

        public ReportOperatingExpensesControllerTest()
        {
            _seed = new RevenueSeed(() => _repositoryContext.GetDatabaseConnection());
        }

        public async Task InitializeAsync()
        {
            foreach (var query in new[] { MonthQuery, QuarterQuery, YtdQuery, EmptyMonthQuery })
                _baseline[query] = (await GetAsync(query)).Json;

            await _seed.InitializeAsync();
            var inMarch = new DateTime(2092, 3, 15);
            var employee = await _seed.AddEmployeeAsync(120000m, 24000m, 10m);
            await _seed.AddInvoiceAsync(inMarch, 1000m, salespersonId: employee);
            await _seed.AddInvoiceAsync(inMarch, 5000m, lifecycle: "draft", salespersonId: employee);

            await _seed.AddPaymentAsync(inMarch, 500m, "Office Expense");
            await _seed.AddAdjustmentAsync(inMarch, 125m, "Bank Service Charge", direction: "out");
            await _seed.AddAdjustmentAsync(inMarch, 40m, "Card Processing Fee", direction: "out");

            await _seed.AddPaymentAsync(inMarch, 38450m, "Payroll");
            await _seed.AddPaymentAsync(inMarch, 1000m, "Tax Payment");
            await _seed.AddPaymentAsync(inMarch, 2000m, "Loan Payment");
            await _seed.AddPaymentAsync(inMarch, 300m, "Customer Refund");
            await _seed.AddPaymentAsync(inMarch, 4000m, "Transfer");
            var bill = await _seed.AddApBillAsync(8000m, 8000m);
            await _seed.AddPaymentAsync(inMarch, 8000m, null, apBillId: bill);
            var vehicle = await _seed.AddVehicleAsync(24000m);
            await _seed.AddPaymentAsync(inMarch, 24000m, null, vehicleId: vehicle);
            var (_, reversedTransaction) = await _seed.AddPaymentAsync(inMarch, 777m, "Office Expense");
            await _seed.AddReversalAsync(reversedTransaction, inMarch, 777m);
            await _seed.AddAdjustmentAsync(inMarch, 625m, "Interest Received", direction: "out");
            await _seed.AddAdjustmentAsync(inMarch, 625m, "Interest Received", direction: "in");
            await _seed.AddStatementOnlyAdjustmentAsync(inMarch, 999m, "Bank Service Charge");
        }

        public Task DisposeAsync() => _seed.CleanupAsync();

        private async Task<(HttpResponseMessage Response, JObject Json)> GetAsync(string query)
        {
            var response = await _testClient.GetAsync($"Report/GetOperatingExpenses?{query}");
            var text = await response.Content.ReadAsStringAsync();
            return (response, response.IsSuccessStatusCode ? JObject.Parse(text) : new JObject());
        }

        private static decimal Amount(JObject json, string name) =>
            (decimal)json.GetValue(name, StringComparison.OrdinalIgnoreCase)!;

        // Rounded to cents: other employees' salaries can leave long decimal fractions that JSON parsing cannot hold exactly.
        private decimal Delta(JObject json, string query, string name) =>
            Math.Round(Amount(json, name) - Amount(_baseline[query], name), 2);

        // ---- 200, response shape ----

        [Fact]
        public async Task GetOperatingExpenses_Returns200WithTheDocumentedColumns()
        {
            var (response, json) = await GetAsync(MonthQuery);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            foreach (var name in new[] { "period_Type", "start_Date", "end_Date", "salary_Expense", "bonus_Expense", "commission_Expense",
                                         "categorized_Payments", "bank_Charges", "total_Operating_Expenses" })
                Assert.NotNull(json.GetValue(name, StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task GetOperatingExpenses_Month_ReturnsThePeriod()
        {
            var (_, json) = await GetAsync(MonthQuery);

            Assert.Equal("Month", json.GetValue("period_Type", StringComparison.OrdinalIgnoreCase)!.ToString());
            Assert.Equal(new DateTime(2092, 3, 1), (DateTime)json.GetValue("start_Date", StringComparison.OrdinalIgnoreCase)!);
            Assert.Equal(new DateTime(2092, 3, 31), (DateTime)json.GetValue("end_Date", StringComparison.OrdinalIgnoreCase)!);
        }

        // ---- AC1: payroll ----

        [Fact]
        public async Task GetOperatingExpenses_Month_AddsOneMonthOfSalaryBonusAndTheCommission()
        {
            var (_, json) = await GetAsync(MonthQuery);

            Assert.Equal(10000m, Delta(json, MonthQuery, "salary_Expense"));
            Assert.Equal(2000m, Delta(json, MonthQuery, "bonus_Expense"));
            Assert.Equal(100m, Delta(json, MonthQuery, "commission_Expense"));
        }

        [Fact]
        public async Task GetOperatingExpenses_Quarter_ProratesSalaryAndBonusByThreeMonths()
        {
            var (_, json) = await GetAsync(QuarterQuery);

            Assert.Equal(30000m, Delta(json, QuarterQuery, "salary_Expense"));
            Assert.Equal(6000m, Delta(json, QuarterQuery, "bonus_Expense"));
            Assert.Equal(100m, Delta(json, QuarterQuery, "commission_Expense"));
        }

        [Fact]
        public async Task GetOperatingExpenses_YearToDateInMarch_ProratesByThreeMonths()
        {
            var (_, json) = await GetAsync(YtdQuery);

            Assert.Equal(30000m, Delta(json, YtdQuery, "salary_Expense"));
            Assert.Equal(6000m, Delta(json, YtdQuery, "bonus_Expense"));
        }

        // ---- AC2-AC5: payments and bank charges ----

        [Fact]
        public async Task GetOperatingExpenses_CountsTheCategorizedPaymentAndTheBankCharges()
        {
            var (_, json) = await GetAsync(MonthQuery);

            Assert.Equal(500m, Delta(json, MonthQuery, "categorized_Payments"));
            Assert.Equal(165m, Delta(json, MonthQuery, "bank_Charges"));
        }

        [Fact]
        public async Task GetOperatingExpenses_ExcludesInventoryLoanTaxTransfersRefundsReversalsAndInterest()
        {
            var (_, json) = await GetAsync(MonthQuery);

            // Only the 500 payment and the 125 + 40 charges are expenses; everything else seeded is excluded.
            var nonPayroll = Delta(json, MonthQuery, "categorized_Payments") + Delta(json, MonthQuery, "bank_Charges");
            Assert.Equal(665m, nonPayroll);
        }

        [Fact]
        public async Task GetOperatingExpenses_TotalIsTheSumOfTheFiveParts()
        {
            var (_, json) = await GetAsync(MonthQuery);

            // 10 000 + 2 000 + 100 + 500 + 165
            Assert.Equal(12765m, Delta(json, MonthQuery, "total_Operating_Expenses"));
        }

        [Fact]
        public async Task GetOperatingExpenses_YearToDateEndingBeforeThePayments_ExcludesThem()
        {
            var (_, json) = await GetAsync("Period_Type=YearToDate&Anchor_Date=2092-03-14");

            Assert.Equal(0m, Amount(json, "categorized_Payments"));
            Assert.Equal(0m, Amount(json, "bank_Charges"));
        }

        // ---- Period with no expenses: 200, not 404 ----

        [Fact]
        public async Task GetOperatingExpenses_PeriodWithNoPaymentsOrSales_Returns200WithNoPaymentCommissionOrCharges()
        {
            var (response, json) = await GetAsync(EmptyMonthQuery);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(0m, Amount(json, "categorized_Payments"));
            Assert.Equal(0m, Amount(json, "bank_Charges"));
            Assert.Equal(0m, Delta(json, EmptyMonthQuery, "commission_Expense"));
        }

        // ---- Invalid DTO -> 400 ----

        [Theory]
        [InlineData("Anchor_Date=2092-03-15")]
        [InlineData("Period_Type=Month")]
        [InlineData("")]
        [InlineData("Period_Type=Week&Anchor_Date=2092-03-15")]
        [InlineData("Period_Type=month&Anchor_Date=2092-03-15")]
        [InlineData("Period_Type=Month&Anchor_Date=not-a-date")]
        public async Task GetOperatingExpenses_InvalidPeriod_Returns400(string query)
        {
            var (response, _) = await GetAsync(query);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        // ---- Read-only ----

        [Theory]
        [InlineData("POST")]
        [InlineData("PUT")]
        [InlineData("DELETE")]
        public async Task GetOperatingExpenses_WriteVerbs_AreNotAllowed(string method)
        {
            var request = new HttpRequestMessage(new HttpMethod(method), $"Report/GetOperatingExpenses?{MonthQuery}");

            var response = await _testClient.SendAsync(request);

            Assert.Contains(response.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
        }
    }
}
