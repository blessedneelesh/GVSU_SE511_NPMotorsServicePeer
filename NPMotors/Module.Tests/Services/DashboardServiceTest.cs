using NPMotors.Repository.Interface;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.RequestFeatures;
using NPMotors.Services.Abstractions.ServiceInterfaces;
using NPMotors.Services.Service;

namespace Module.Tests.Services
{
    // Dashboard summary. BuildMonthlyRevenue is private static and is tested only through GetSummary.
    // Revenue, vehicles sold and the change % come from the real ReportService (ComparePeriods) over a fake report
    // repository keyed by the period's start date; the dashboard repository and the invoice service are fakes.
    public class DashboardServiceTest
    {
        private class PeriodData
        {
            public SalesRevenueTotalsDto Sales { get; } = new();
            public List<SoldVehicleCostDto> Vehicles { get; } = new();
        }

        private class FakeReportRepository : IReportRepository
        {
            public Dictionary<DateTime, PeriodData> Periods { get; } = new();

            private PeriodData Data(DateTime start) => Periods.TryGetValue(start, out var data) ? data : new PeriodData();

            public Task<SalesRevenueTotalsDto> GetSalesRevenue(DateTime start, DateTime end) => Task.FromResult(Data(start).Sales);
            public Task<List<SoldVehicleCostDto>> GetSoldVehicleCosts(DateTime start, DateTime end) => Task.FromResult(Data(start).Vehicles);
            public Task<List<EmployeePayrollDto>> GetEmployeePayroll(DateTime start, DateTime end) => Task.FromResult(new List<EmployeePayrollDto>());
            public Task<List<IncomeAdjustmentDto>> GetIncomeAdjustments(DateTime start, DateTime end) => Task.FromResult(new List<IncomeAdjustmentDto>());
            public Task<List<ExpenseCandidateDto>> GetExpenseCandidates(DateTime start, DateTime end) => Task.FromResult(new List<ExpenseCandidateDto>());
        }

        private class FakeDashboardRepository : IDashboardRepository
        {
            public InventoryCountsDto Inventory { get; set; } = new();
            public List<MakeCountDto> ByMake { get; set; } = new();
            public ReceivableTotalsDto Receivables { get; set; } = new();
            public int ActiveCustomers { get; set; }
            public List<MonthlyRevenueComponentsDto> Components { get; set; } = new();
            public List<(DateTime Start, DateTime End)> MonthlyCalls { get; } = new();

            public Task<InventoryCountsDto> GetInventoryCounts() => Task.FromResult(Inventory);
            public Task<List<MakeCountDto>> GetAvailableByMake() => Task.FromResult(ByMake);
            public Task<ReceivableTotalsDto> GetReceivableTotals() => Task.FromResult(Receivables);
            public Task<int> GetActiveCustomerCount() => Task.FromResult(ActiveCustomers);

            public Task<List<MonthlyRevenueComponentsDto>> GetMonthlyRevenueComponents(DateTime start, DateTime end)
            {
                MonthlyCalls.Add((start, end));
                return Task.FromResult(Components);
            }
        }

        private class FakeSalesInvoiceService : ISalesInvoiceService
        {
            public List<SalesInvoiceResponseDto> Invoices { get; set; } = new();
            public List<SalesInvoiceParameters> Calls { get; } = new();

            public Task<PagedList<SalesInvoiceResponseDto>> GetAllSalesInvoices(SalesInvoiceParameters parameters)
            {
                Calls.Add(parameters);
                return Task.FromResult(new PagedList<SalesInvoiceResponseDto>(Invoices, Invoices.Count, parameters.PageNumber, parameters.PageSize));
            }

            public Task<SalesInvoiceResponseDto> AddSalesInvoice(SalesInvoiceCreateDto invoice) => throw new NotImplementedException();
            public Task<SalesInvoiceResponseDto> PostSalesInvoice(SalesInvoiceCreateDto invoice) => throw new NotImplementedException();
            public Task<SalesInvoiceResponseDto?> GetSalesInvoiceById(long id) => throw new NotImplementedException();
            public Task<List<SalesInvoiceByCustomerDto>> GetSalesInvoiceByCustomer(long customerId) => throw new NotImplementedException();
            public Task<SalesInvoiceResponseDto> CancelSalesInvoice(long id, SalesInvoiceCancelDto reason) => throw new NotImplementedException();
            public Task<SalesInvoiceResponseDto?> PostInvoice(long id, PostInvoiceRequest request) => throw new NotImplementedException();
            public Task<SalesInvoiceResponseDto?> UpdateDraft(long id, SalesInvoiceCreateDto invoice) => throw new NotImplementedException();
            public Task<SalesInvoiceResponseDto> CreateAndPostInvoice(SalesInvoiceCreateAndPostRequest request) => throw new NotImplementedException();
        }

        private readonly FakeReportRepository _reports = new();
        private readonly FakeDashboardRepository _dashboard = new();
        private readonly FakeSalesInvoiceService _invoices = new();

        private DashboardService CreateService() => new(_dashboard, new ReportService(_reports), _invoices);

        private static readonly DateTime AsOf = new(2026, 11, 26);

        private void Period(DateTime start, decimal revenue, params long[] soldVehicleIds)
        {
            var data = new PeriodData();
            data.Sales.Vehicle_Revenue = revenue;
            foreach (var id in soldVehicleIds)
                data.Vehicles.Add(new SoldVehicleCostDto { Vehicle_Id = id, Total_Cost = 1000m });
            _reports.Periods[start] = data;
        }

        private static MonthlyRevenueComponentsDto Month(int year, int month, decimal vehicle = 0m, decimal fees = 0m,
            decimal discounts = 0m, decimal interest = 0m, decimal other = 0m) =>
            new()
            {
                Month = new DateTime(year, month, 1), Vehicle_Revenue = vehicle, Fee_Income = fees, Discounts = discounts,
                Interest_Income = interest, Other_Income = other
            };

        // ---- Period header ----

        [Fact]
        public async Task GetSummary_AsOfNovember_ReturnsThatMonthAsThePeriod()
        {
            var summary = await CreateService().GetSummary(AsOf);

            Assert.Equal(new DateOnly(2026, 11, 26), summary.As_Of_Date);
            Assert.Equal(new DateOnly(2026, 11, 1), summary.Period_Start);
            Assert.Equal(new DateOnly(2026, 11, 30), summary.Period_End);
        }

        [Fact]
        public async Task GetSummary_AsOfLeapFebruary_PeriodEndsOnThe29th()
        {
            var summary = await CreateService().GetSummary(new DateTime(2024, 2, 10, 15, 30, 0));

            Assert.Equal(new DateOnly(2024, 2, 10), summary.As_Of_Date);
            Assert.Equal(new DateOnly(2024, 2, 1), summary.Period_Start);
            Assert.Equal(new DateOnly(2024, 2, 29), summary.Period_End);
        }

        [Fact]
        public async Task GetSummary_NoAsOfDate_UsesToday()
        {
            var today = DateTime.Today;

            var summary = await CreateService().GetSummary(null);

            Assert.Equal(DateOnly.FromDateTime(today), summary.As_Of_Date);
            Assert.Equal(new DateOnly(today.Year, today.Month, 1), summary.Period_Start);
        }

        // ---- Repository figures pass through ----

        [Fact]
        public async Task GetSummary_CopiesCountsAndTotalsFromTheRepository()
        {
            _dashboard.Inventory = new InventoryCountsDto { Total_Inventory = 28, Available_Vehicles = 9 };
            _dashboard.Receivables = new ReceivableTotalsDto { Receivable_Balance = 321651m, Open_Receivables = 14 };
            _dashboard.ActiveCustomers = 16;

            var summary = await CreateService().GetSummary(AsOf);

            Assert.Equal(28, summary.Total_Inventory);
            Assert.Equal(9, summary.Available_Vehicles);
            Assert.Equal(321651m, summary.Receivable_Balance);
            Assert.Equal(14, summary.Open_Receivables);
            Assert.Equal(16, summary.Active_Customers);
        }

        [Fact]
        public async Task GetSummary_ReturnsEveryMakeInTheRepositoryOrder()
        {
            _dashboard.ByMake = new List<MakeCountDto>
            {
                new() { Make = "Toyota", Count = 5 }, new() { Make = "Honda", Count = 2 }, new() { Make = "Kia", Count = 1 },
                new() { Make = "Mazda", Count = 1 }, new() { Make = "Unknown", Count = 1 }
            };

            var summary = await CreateService().GetSummary(AsOf);

            Assert.Equal(new[] { "Toyota", "Honda", "Kia", "Mazda", "Unknown" }, summary.Available_By_Make.Select(m => m.Make));
            Assert.Equal(new[] { 5, 2, 1, 1, 1 }, summary.Available_By_Make.Select(m => m.Count));
        }

        // ---- Revenue, vehicles sold, change % (ComparePeriods) ----

        [Fact]
        public async Task GetSummary_RevenueAndPreviousRevenue_AreThisAndLastMonth()
        {
            Period(new DateTime(2026, 11, 1), 30125m);
            Period(new DateTime(2026, 10, 1), 25000m);

            var summary = await CreateService().GetSummary(AsOf);

            Assert.Equal(30125m, summary.Revenue);
            Assert.Equal(25000m, summary.Revenue_Previous);
        }

        [Fact]
        public async Task GetSummary_RevenueChange_IsPercentOfPrevious()
        {
            // (30 125 - 25 000) / 25 000 x 100 = 20.5.
            Period(new DateTime(2026, 11, 1), 30125m);
            Period(new DateTime(2026, 10, 1), 25000m);

            var summary = await CreateService().GetSummary(AsOf);

            Assert.Equal(20.5m, summary.Revenue_Change_Pct);
        }

        [Fact]
        public async Task GetSummary_RevenueDecline_GivesNegativeChange()
        {
            // (15 000 - 20 000) / 20 000 x 100 = -25.
            Period(new DateTime(2026, 11, 1), 15000m);
            Period(new DateTime(2026, 10, 1), 20000m);

            var summary = await CreateService().GetSummary(AsOf);

            Assert.Equal(-25m, summary.Revenue_Change_Pct);
        }

        [Fact]
        public async Task GetSummary_PreviousRevenueZero_ChangeIsNull()
        {
            Period(new DateTime(2026, 11, 1), 370950m);

            var summary = await CreateService().GetSummary(AsOf);

            Assert.Equal(370950m, summary.Revenue);
            Assert.Equal(0m, summary.Revenue_Previous);
            Assert.Null(summary.Revenue_Change_Pct);
        }

        [Fact]
        public async Task GetSummary_VehiclesSold_CountsDistinctVehiclesThisAndLastMonth()
        {
            Period(new DateTime(2026, 11, 1), 0m, 1, 2, 3);
            Period(new DateTime(2026, 10, 1), 0m, 4);

            var summary = await CreateService().GetSummary(AsOf);

            Assert.Equal(3, summary.Vehicles_Sold);
            Assert.Equal(1, summary.Vehicles_Sold_Previous);
        }

        [Fact]
        public async Task GetSummary_AsOfJanuary_PreviousMonthIsDecember()
        {
            Period(new DateTime(2027, 1, 1), 500m);
            Period(new DateTime(2026, 12, 1), 400m);

            var summary = await CreateService().GetSummary(new DateTime(2027, 1, 5));

            Assert.Equal(500m, summary.Revenue);
            Assert.Equal(400m, summary.Revenue_Previous);
            Assert.Equal(25m, summary.Revenue_Change_Pct);
        }

        // ---- Monthly revenue (BuildMonthlyRevenue) ----

        [Fact]
        public async Task GetSummary_AsksForSevenMonthsEndingWithTheAsOfMonth()
        {
            await CreateService().GetSummary(AsOf);

            var call = Assert.Single(_dashboard.MonthlyCalls);
            Assert.Equal(new DateTime(2026, 5, 1), call.Start);
            Assert.Equal(new DateTime(2026, 11, 30), call.End);
        }

        [Fact]
        public async Task GetSummary_MonthlyWindowAcrossNewYear_StartsInAugust()
        {
            await CreateService().GetSummary(new DateTime(2027, 2, 10));

            var call = Assert.Single(_dashboard.MonthlyCalls);
            Assert.Equal(new DateTime(2026, 8, 1), call.Start);
            Assert.Equal(new DateTime(2027, 2, 28), call.End);
        }

        [Fact]
        public async Task GetSummary_MonthlyRevenue_IsVehiclePlusFeesPlusInterestPlusOtherMinusDiscounts()
        {
            // 10 000 + 500 + 100 + 50 - 200 = 10 450.
            _dashboard.Components = new List<MonthlyRevenueComponentsDto>
            {
                Month(2026, 11, vehicle: 10000m, fees: 500m, discounts: 200m, interest: 100m, other: 50m)
            };

            var summary = await CreateService().GetSummary(AsOf);

            var month = Assert.Single(summary.Monthly_Revenue);
            Assert.Equal("2026-11", month.Month);
            Assert.Equal(10450m, month.Revenue);
        }

        [Fact]
        public async Task GetSummary_MonthlyRevenue_DiscountsLargerThanSales_IsNegative()
        {
            // 1 000 - 1 500 = -500, not clamped.
            _dashboard.Components = new List<MonthlyRevenueComponentsDto> { Month(2026, 10, vehicle: 1000m, discounts: 1500m) };

            var summary = await CreateService().GetSummary(AsOf);

            Assert.Equal(-500m, Assert.Single(summary.Monthly_Revenue).Revenue);
        }

        [Fact]
        public async Task GetSummary_MonthlyRevenue_IsOldestFirstWithEveryMonth()
        {
            _dashboard.Components = new List<MonthlyRevenueComponentsDto>
            {
                Month(2026, 11, vehicle: 370950m), Month(2026, 5), Month(2026, 9), Month(2026, 6), Month(2026, 10), Month(2026, 7), Month(2026, 8)
            };

            var summary = await CreateService().GetSummary(AsOf);

            Assert.Equal(new[] { "2026-05", "2026-06", "2026-07", "2026-08", "2026-09", "2026-10", "2026-11" },
                summary.Monthly_Revenue.Select(m => m.Month));
            Assert.Equal(new[] { 0m, 0m, 0m, 0m, 0m, 0m, 370950m }, summary.Monthly_Revenue.Select(m => m.Revenue));
        }

        [Fact]
        public async Task GetSummary_MonthlyRevenueAcrossNewYear_UsesYearInTheLabel()
        {
            _dashboard.Components = new List<MonthlyRevenueComponentsDto> { Month(2027, 1, vehicle: 1m), Month(2026, 12, vehicle: 2m) };

            var summary = await CreateService().GetSummary(new DateTime(2027, 1, 5));

            Assert.Equal(new[] { "2026-12", "2027-01" }, summary.Monthly_Revenue.Select(m => m.Month));
        }

        [Fact]
        public async Task GetSummary_LastMonthlyEntry_EqualsTheHeadlineRevenue()
        {
            Period(new DateTime(2026, 11, 1), 30125m);
            _dashboard.Components = new List<MonthlyRevenueComponentsDto> { Month(2026, 11, vehicle: 30125m) };

            var summary = await CreateService().GetSummary(AsOf);

            Assert.Equal(summary.Revenue, summary.Monthly_Revenue.Last().Revenue);
        }

        // ---- Recent invoices ----

        [Fact]
        public async Task GetSummary_RecentInvoices_AsksForTheFourNewestPostedInvoices()
        {
            await CreateService().GetSummary(AsOf);

            var call = Assert.Single(_invoices.Calls);
            Assert.Equal("posted", call.Lifecycle);
            Assert.Equal(4, call.PageSize);
            Assert.Equal(1, call.PageNumber);
            Assert.Null(call.Status);
            Assert.Null(call.SearchTerm);
        }

        [Fact]
        public async Task GetSummary_RecentInvoices_AreReturnedInTheServiceOrder()
        {
            _invoices.Invoices = new List<SalesInvoiceResponseDto>
            {
                new() { Id = 9, Number = "INV-000009", Customer_Name = "Ann Lee", Total = 100m, Balance = 40m },
                new() { Id = 7, Number = "INV-000007", Customer_Name = "Bo Kim", Total = 200m, Balance = 0m }
            };

            var summary = await CreateService().GetSummary(AsOf);

            Assert.Equal(new long[] { 9, 7 }, summary.Recent_Invoices.Select(i => i.Id));
            Assert.Equal(40m, summary.Recent_Invoices[0].Balance);
        }

        [Fact]
        public async Task GetSummary_NoData_ReturnsZerosAndEmptyLists()
        {
            var summary = await CreateService().GetSummary(AsOf);

            Assert.Equal(0, summary.Total_Inventory);
            Assert.Equal(0, summary.Vehicles_Sold);
            Assert.Equal(0m, summary.Revenue);
            Assert.Null(summary.Revenue_Change_Pct);
            Assert.Equal(0m, summary.Receivable_Balance);
            Assert.Empty(summary.Available_By_Make);
            Assert.Empty(summary.Monthly_Revenue);
            Assert.Empty(summary.Recent_Invoices);
        }
    }
}
