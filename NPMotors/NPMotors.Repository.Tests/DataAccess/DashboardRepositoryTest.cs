using Dapper;
using NPMotors.Repository.Repositories;

namespace NPMotors.Repository.Tests.DataAccess
{
    // DashboardRepository. The vehicle, AR and customer tables hold other data, so those tests compare the figures
    // before and after inserting marked rows (deltas). The monthly revenue tests use 2095, which no other test uses.
    public class DashboardRepositoryTest : TestBase, IAsyncLifetime
    {
        private readonly DashboardRepository _repository;
        private readonly RevenueSeed _seed;

        public DashboardRepositoryTest()
        {
            _repository = new DashboardRepository(_repositoryContext);
            _seed = new RevenueSeed(() => _repositoryContext.GetDatabaseConnection());
        }

        public Task InitializeAsync() => _seed.InitializeAsync();

        public async Task DisposeAsync()
        {
            using var c = _repositoryContext.GetDatabaseConnection();
            await c.ExecuteAsync("DELETE FROM ar_invoice WHERE number LIKE @Pattern", new { Pattern = _seed.Marker + "%" });
            await _seed.CleanupAsync();
        }

        private async Task<long> AddVehicleAsync(string? make, string? status)
        {
            using var c = _repositoryContext.GetDatabaseConnection();
            return await c.ExecuteScalarAsync<long>(
                @"INSERT INTO vehicle (vin, vendor_id, make, model, purchase_price, additional_costs, total_cost, selling_price, status, created_by)
                  VALUES (@Vin, @VendorId, @Make, 'Civic', 1000, 0, 1000, 2000, @Status, 'itest') RETURNING id",
                new { Vin = Guid.NewGuid().ToString("N").Substring(0, 17).ToUpperInvariant(), _seed.VendorId, Make = make, Status = status });
        }

        private async Task AddCustomerAsync(string status)
        {
            using var c = _repositoryContext.GetDatabaseConnection();
            await c.ExecuteAsync("INSERT INTO customer (first_name, last_name, status) VALUES (@Name, 'Dash', @Status)",
                new { Name = _seed.Marker, Status = status });
        }

        private async Task AddArAsync(decimal amount, decimal paid)
        {
            var invoiceId = await _seed.AddInvoiceAsync(new DateTime(2095, 1, 5), amount);
            using var c = _repositoryContext.GetDatabaseConnection();
            await c.ExecuteAsync(
                @"INSERT INTO ar_invoice (invoice_id, number, date, due_date, amount, paid, balance, status, created_at, created_by)
                  VALUES (@InvoiceId, @Number, @Date, @Date, @Amount, @Paid, @Balance, @Status, NOW(), 'itest')",
                new
                {
                    InvoiceId = invoiceId, Number = $"{_seed.Marker}-AR-{invoiceId}", Date = new DateTime(2095, 1, 5), Amount = amount,
                    Paid = paid, Balance = amount - paid, Status = paid >= amount ? "Paid" : paid > 0 ? "Partial" : "Unpaid"
                });
        }

        // ---- GetInventoryCounts ----

        [Fact]
        public async Task GetInventoryCounts_CountsEveryVehicleAndTheAvailableOnes()
        {
            var before = await _repository.GetInventoryCounts();
            await AddVehicleAsync("Honda", "available");
            await AddVehicleAsync("Honda", "AVAILABLE");
            await AddVehicleAsync("Honda", "reserved");
            await AddVehicleAsync("Honda", "sold");

            var after = await _repository.GetInventoryCounts();

            Assert.Equal(4, after.Total_Inventory - before.Total_Inventory);
            Assert.Equal(2, after.Available_Vehicles - before.Available_Vehicles);
        }

        [Fact]
        public async Task GetInventoryCounts_VehicleWithNullStatus_CountsInTotalOnly()
        {
            var before = await _repository.GetInventoryCounts();
            await AddVehicleAsync("Honda", null);

            var after = await _repository.GetInventoryCounts();

            Assert.Equal(1, after.Total_Inventory - before.Total_Inventory);
            Assert.Equal(0, after.Available_Vehicles - before.Available_Vehicles);
        }

        // ---- GetAvailableByMake ----

        [Fact]
        public async Task GetAvailableByMake_IgnoresCaseAndSpaces_AndCountsAvailableOnly()
        {
            var make = _seed.Marker + "mk";
            await AddVehicleAsync(make, "available");
            await AddVehicleAsync($"  {make.ToUpperInvariant()} ", "available");
            await AddVehicleAsync(make, "reserved");
            await AddVehicleAsync(make, "sold");

            var result = await _repository.GetAvailableByMake();

            var group = Assert.Single(result, m => string.Equals(m.Make, make, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(2, group.Count);
            Assert.Equal(group.Make.Trim(), group.Make);
        }

        [Fact]
        public async Task GetAvailableByMake_BlankAndNullMakes_AreUnknown()
        {
            var before = (await _repository.GetAvailableByMake()).FirstOrDefault(m => m.Make == "Unknown")?.Count ?? 0;
            await AddVehicleAsync(null, "available");
            await AddVehicleAsync("", "available");
            await AddVehicleAsync("   ", "available");
            await AddVehicleAsync(null, "reserved");

            var result = await _repository.GetAvailableByMake();

            var unknown = Assert.Single(result, m => m.Make == "Unknown");
            Assert.Equal(before + 3, unknown.Count);
        }

        [Fact]
        public async Task GetAvailableByMake_IsSortedByCountDescendingThenMake()
        {
            await AddVehicleAsync(_seed.Marker + "b", "available");
            await AddVehicleAsync(_seed.Marker + "a", "available");
            await AddVehicleAsync(_seed.Marker + "c", "available");
            await AddVehicleAsync(_seed.Marker + "c", "available");

            var result = await _repository.GetAvailableByMake();

            for (var i = 1; i < result.Count; i++)
                Assert.True(result[i - 1].Count > result[i].Count
                            || (result[i - 1].Count == result[i].Count
                                && string.Compare(result[i - 1].Make, result[i].Make, StringComparison.OrdinalIgnoreCase) <= 0),
                    $"{result[i - 1].Make} ({result[i - 1].Count}) must come before {result[i].Make} ({result[i].Count})");
            var mine = result.Where(m => m.Make.StartsWith(_seed.Marker)).Select(m => m.Make.Substring(_seed.Marker.Length)).ToList();
            Assert.Equal(new[] { "c", "a", "b" }, mine);
        }

        [Fact]
        public async Task GetAvailableByMake_ReturnsEveryMake_NotOnlyTheTopFour()
        {
            foreach (var suffix in new[] { "1", "2", "3", "4", "5", "6" })
                await AddVehicleAsync(_seed.Marker + suffix, "available");

            var result = await _repository.GetAvailableByMake();

            Assert.Equal(6, result.Count(m => m.Make.StartsWith(_seed.Marker)));
        }

        // ---- GetReceivableTotals ----

        [Fact]
        public async Task GetReceivableTotals_SumsBalanceAndCountsEveryArRow()
        {
            var before = await _repository.GetReceivableTotals();
            await AddArAsync(1000m, 0m);
            await AddArAsync(500m, 200m);
            await AddArAsync(300m, 300m);

            var after = await _repository.GetReceivableTotals();

            // Balances 1 000 + 300 + 0; the paid row still counts as a row, as in GetAllAr with no Status.
            Assert.Equal(1300m, after.Receivable_Balance - before.Receivable_Balance);
            Assert.Equal(3, after.Open_Receivables - before.Open_Receivables);
        }

        [Fact]
        public async Task GetReceivableTotals_MatchesTheGetAllArSet()
        {
            await AddArAsync(1000m, 0m);

            var totals = await _repository.GetReceivableTotals();
            var all = await new ArInvoiceRepository(_repositoryContext).GetAllAr(new NPMotors.Repository.RequestFeatures.ArAgingParameters { PageSize = 100 });

            Assert.Equal(all.MetaData.TotalCount, totals.Open_Receivables);
        }

        // ---- GetActiveCustomerCount ----

        [Fact]
        public async Task GetActiveCustomerCount_CountsActiveCustomersCaseInsensitively()
        {
            var before = await _repository.GetActiveCustomerCount();
            await AddCustomerAsync("active");
            await AddCustomerAsync("ACTIVE");
            await AddCustomerAsync("inactive");

            var after = await _repository.GetActiveCustomerCount();

            // The customer the seed inserted has no status and is not counted.
            Assert.Equal(2, after - before);
        }

        // ---- GetMonthlyRevenueComponents (2095) ----

        private static readonly DateTime From = new(2095, 3, 1);
        private static readonly DateTime To = new(2095, 5, 31);

        [Fact]
        public async Task GetMonthlyRevenueComponents_ReturnsOneRowPerMonthOldestFirst_ZerosWhenEmpty()
        {
            await _seed.AddInvoiceAsync(new DateTime(2095, 3, 10), 30000m, fees: 500m, discount: 1000m);
            await _seed.AddInvoiceAsync(new DateTime(2095, 3, 20), 12000m, fees: 250m, discount: 200m);
            await _seed.AddInvoiceAsync(new DateTime(2095, 5, 2), 6000m);

            var rows = await _repository.GetMonthlyRevenueComponents(From, To);

            Assert.Equal(new[] { new DateTime(2095, 3, 1), new DateTime(2095, 4, 1), new DateTime(2095, 5, 1) }, rows.Select(r => r.Month));
            Assert.Equal(42000m, rows[0].Vehicle_Revenue);
            Assert.Equal(750m, rows[0].Fee_Income);
            Assert.Equal(1200m, rows[0].Discounts);
            Assert.Equal(0m, rows[1].Vehicle_Revenue);
            Assert.Equal(0m, rows[1].Fee_Income);
            Assert.Equal(0m, rows[1].Discounts);
            Assert.Equal(6000m, rows[2].Vehicle_Revenue);
        }

        [Fact]
        public async Task GetMonthlyRevenueComponents_NoData_ReturnsZeroRowsForEveryMonth()
        {
            var rows = await _repository.GetMonthlyRevenueComponents(new DateTime(2095, 6, 1), new DateTime(2095, 8, 31));

            Assert.Equal(3, rows.Count);
            Assert.All(rows, r =>
            {
                Assert.Equal(0m, r.Vehicle_Revenue);
                Assert.Equal(0m, r.Interest_Income);
                Assert.Equal(0m, r.Other_Income);
            });
        }

        [Fact]
        public async Task GetMonthlyRevenueComponents_DraftAndCancelledInvoices_AreExcluded()
        {
            await _seed.AddInvoiceAsync(new DateTime(2095, 3, 10), 30000m, lifecycle: "draft");
            await _seed.AddInvoiceAsync(new DateTime(2095, 3, 11), 20000m, lifecycle: "cancelled");
            await _seed.AddInvoiceAsync(new DateTime(2095, 3, 12), 1000m);

            var rows = await _repository.GetMonthlyRevenueComponents(From, To);

            Assert.Equal(1000m, rows[0].Vehicle_Revenue);
        }

        [Fact]
        public async Task GetMonthlyRevenueComponents_SplitsInterestAndOtherAdjustments_PerMonth()
        {
            await _seed.AddAdjustmentAsync(new DateTime(2095, 3, 5), 625m, "Interest Received");
            await _seed.AddAdjustmentAsync(new DateTime(2095, 3, 6), 75m, "Interest Received");
            await _seed.AddAdjustmentAsync(new DateTime(2095, 5, 7), 40m, "Other Adjustment");
            await _seed.AddAdjustmentAsync(new DateTime(2095, 4, 7), 15m, "Bank Service Charge");

            var rows = await _repository.GetMonthlyRevenueComponents(From, To);

            Assert.Equal(700m, rows[0].Interest_Income);
            Assert.Equal(0m, rows[0].Other_Income);
            Assert.Equal(0m, rows[1].Interest_Income);
            Assert.Equal(0m, rows[1].Other_Income);
            Assert.Equal(0m, rows[2].Interest_Income);
            Assert.Equal(40m, rows[2].Other_Income);
        }

        [Fact]
        public async Task GetMonthlyRevenueComponents_OutgoingAndReversedAdjustments_AreExcluded()
        {
            await _seed.AddAdjustmentAsync(new DateTime(2095, 3, 5), 100m, "Other Adjustment", direction: "out");
            var reversed = await _seed.AddAdjustmentAsync(new DateTime(2095, 3, 6), 200m, "Other Adjustment");
            await _seed.AddReversalAsync(reversed, new DateTime(2095, 3, 7), 200m);
            await _seed.AddAdjustmentAsync(new DateTime(2095, 3, 8), 30m, "Other Adjustment");

            var rows = await _repository.GetMonthlyRevenueComponents(From, To);

            Assert.Equal(30m, rows[0].Other_Income);
        }

        [Fact]
        public async Task GetMonthlyRevenueComponents_AgreesWithTheReportRepositoryForOneMonth()
        {
            await _seed.AddInvoiceAsync(new DateTime(2095, 3, 10), 30000m, fees: 500m, discount: 1000m);
            await _seed.AddAdjustmentAsync(new DateTime(2095, 3, 5), 625m, "Interest Received");
            var report = new ReportRepository(_repositoryContext);

            var rows = await _repository.GetMonthlyRevenueComponents(From, To);
            var sales = await report.GetSalesRevenue(From, new DateTime(2095, 3, 31));
            var adjustments = await report.GetIncomeAdjustments(From, new DateTime(2095, 3, 31));

            Assert.Equal(sales.Vehicle_Revenue, rows[0].Vehicle_Revenue);
            Assert.Equal(sales.Fee_Income, rows[0].Fee_Income);
            Assert.Equal(sales.Discounts, rows[0].Discounts);
            Assert.Equal(adjustments.Where(a => a.Adjustment_Type == "Interest Received").Sum(a => a.Amount), rows[0].Interest_Income);
        }

        [Fact]
        public async Task GetMonthlyRevenueComponents_SpansNewYear()
        {
            await _seed.AddInvoiceAsync(new DateTime(2095, 12, 20), 100m);
            await _seed.AddInvoiceAsync(new DateTime(2096, 1, 3), 200m);

            var rows = await _repository.GetMonthlyRevenueComponents(new DateTime(2095, 11, 1), new DateTime(2096, 1, 31));

            Assert.Equal(new[] { new DateTime(2095, 11, 1), new DateTime(2095, 12, 1), new DateTime(2096, 1, 1) }, rows.Select(r => r.Month));
            Assert.Equal(new[] { 0m, 100m, 200m }, rows.Select(r => r.Vehicle_Revenue));
        }
    }
}
