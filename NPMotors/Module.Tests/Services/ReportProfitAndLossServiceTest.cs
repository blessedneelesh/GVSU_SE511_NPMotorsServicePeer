using System.ComponentModel.DataAnnotations;
using NPMotors.Repository.Interface;
using NPMotors.Repository.Models.Dto;
using NPMotors.Services.Service;

namespace Module.Tests.Services
{
    // Story 29, GetProfitAndLoss. CalculateGrossProfit, CalculateNetProfit and CalculateMargin are private static;
    // they are tested only through GetProfitAndLoss. The Story 26-28 figures come from a fake repository.
    public class ReportProfitAndLossServiceTest
    {
        // Hand-written fake: the test projects do not use a mocking library.
        private class FakeReportRepository : IReportRepository
        {
            public SalesRevenueTotalsDto Sales { get; set; } = new();
            public List<IncomeAdjustmentDto> Adjustments { get; set; } = new();
            public List<SoldVehicleCostDto> Vehicles { get; set; } = new();
            public List<EmployeePayrollDto> Employees { get; set; } = new();
            public List<ExpenseCandidateDto> Candidates { get; set; } = new();

            public List<(DateTime Start, DateTime End)> SalesCalls { get; } = new();
            public List<(DateTime Start, DateTime End)> AdjustmentCalls { get; } = new();
            public List<(DateTime Start, DateTime End)> VehicleCalls { get; } = new();
            public List<(DateTime Start, DateTime End)> PayrollCalls { get; } = new();
            public List<(DateTime Start, DateTime End)> CandidateCalls { get; } = new();

            public Task<SalesRevenueTotalsDto> GetSalesRevenue(DateTime start, DateTime end)
            {
                SalesCalls.Add((start, end));
                return Task.FromResult(Sales);
            }

            public Task<List<IncomeAdjustmentDto>> GetIncomeAdjustments(DateTime start, DateTime end)
            {
                AdjustmentCalls.Add((start, end));
                return Task.FromResult(Adjustments);
            }

            public Task<List<SoldVehicleCostDto>> GetSoldVehicleCosts(DateTime start, DateTime end)
            {
                VehicleCalls.Add((start, end));
                return Task.FromResult(Vehicles);
            }

            public Task<List<EmployeePayrollDto>> GetEmployeePayroll(DateTime start, DateTime end)
            {
                PayrollCalls.Add((start, end));
                return Task.FromResult(Employees);
            }

            public Task<List<ExpenseCandidateDto>> GetExpenseCandidates(DateTime start, DateTime end)
            {
                CandidateCalls.Add((start, end));
                return Task.FromResult(Candidates);
            }
        }

        private readonly FakeReportRepository _repository = new();

        private ReportService CreateService() => new(_repository);

        // Sets the three Story 26-28 totals for a Month period: revenue (vehicle revenue only), COGS (one vehicle's cost)
        // and operating expenses (one employee whose annual salary / 12 equals the amount).
        private void Figures(decimal revenue, decimal cogs, decimal operatingExpenses)
        {
            _repository.Sales = new SalesRevenueTotalsDto { Vehicle_Revenue = revenue };
            _repository.Vehicles = new()
            {
                new SoldVehicleCostDto { Vehicle_Id = 1, Vin = "VIN", Invoice_Id = 1, Invoice_Number = "INV-1", Total_Cost = cogs }
            };
            _repository.Employees = new()
            {
                new EmployeePayrollDto { Employee_Id = 1, Employee_Name = "Ann Lee", Salary = operatingExpenses * 12m }
            };
        }

        private Task<ProfitAndLossDto> ReportAsync(string type = "Month", DateTime? anchor = null, string generatedBy = "ann") =>
            CreateService().GetProfitAndLoss(
                new ReportPeriodDto { Period_Type = type, Anchor_Date = anchor ?? new DateTime(2024, 3, 15) }, generatedBy);

        // ---- Story example ----

        [Fact]
        public async Task GetProfitAndLoss_StoryExample_GrossProfit4925AndNetProfit1925()
        {
            // Revenue 30 000 + fees 500 - discounts 1 000 + interest 625 = 30 125; COGS 25 200; operating expenses 3 000.
            _repository.Sales = new SalesRevenueTotalsDto { Vehicle_Revenue = 30000m, Fee_Income = 500m, Discounts = 1000m, Invoice_Count = 1 };
            _repository.Adjustments = new()
            {
                new IncomeAdjustmentDto { Transaction_Id = 1, Number = "TXN-1", Amount = 625m, Adjustment_Type = "Interest Received" }
            };
            _repository.Vehicles = new() { new SoldVehicleCostDto { Vehicle_Id = 1, Total_Cost = 25200m } };
            _repository.Employees = new() { new EmployeePayrollDto { Employee_Id = 1, Salary = 36000m } };

            var report = await ReportAsync();

            Assert.Equal(30125m, report.Total_Revenue);
            Assert.Equal(25200m, report.Total_Cogs);
            Assert.Equal(4925m, report.Gross_Profit);
            Assert.Equal(16.35m, report.Gross_Margin);
            Assert.Equal(3000m, report.Total_Operating_Expenses);
            Assert.Equal(1925m, report.Net_Profit);
            Assert.Equal(6.39m, report.Net_Margin);
        }

        // ---- Totals copied from the Story 26-28 summaries (AC1) ----

        [Fact]
        public async Task GetProfitAndLoss_TotalsAreCopiedFromTheThreeSummaries()
        {
            Figures(revenue: 1000m, cogs: 400m, operatingExpenses: 250m);

            var report = await ReportAsync();

            Assert.Equal(1000m, report.Total_Revenue);
            Assert.Equal(400m, report.Total_Cogs);
            Assert.Equal(250m, report.Total_Operating_Expenses);
        }

        [Fact]
        public async Task GetProfitAndLoss_IncludesTheThreeSummariesInFull()
        {
            Figures(revenue: 1000m, cogs: 400m, operatingExpenses: 250m);

            var report = await ReportAsync();

            Assert.Equal(1000m, report.Revenue.Vehicle_Revenue);
            Assert.Equal(1000m, report.Revenue.Total_Revenue);
            Assert.Equal(400m, report.Cogs.Vehicle_Cogs);
            Assert.Equal(400m, report.Cogs.Total_Cogs);
            Assert.Equal(250m, report.Operating_Expenses.Salary_Expense);
            Assert.Equal(250m, report.Operating_Expenses.Total_Operating_Expenses);
        }

        // ---- CalculateGrossProfit ----

        [Fact]
        public async Task GetProfitAndLoss_GrossProfitIsRevenueMinusCogs()
        {
            Figures(revenue: 1000m, cogs: 400m, operatingExpenses: 0m);

            var report = await ReportAsync();

            Assert.Equal(600m, report.Gross_Profit);
        }

        [Fact]
        public async Task GetProfitAndLoss_GrossProfitIgnoresOperatingExpenses()
        {
            Figures(revenue: 1000m, cogs: 400m, operatingExpenses: 250m);

            var report = await ReportAsync();

            Assert.Equal(600m, report.Gross_Profit);
        }

        [Fact]
        public async Task GetProfitAndLoss_CogsAboveRevenue_GivesNegativeGrossProfit()
        {
            Figures(revenue: 1000m, cogs: 1500m, operatingExpenses: 0m);

            var report = await ReportAsync();

            Assert.Equal(-500m, report.Gross_Profit);
        }

        // ---- CalculateNetProfit ----

        [Fact]
        public async Task GetProfitAndLoss_NetProfitIsGrossProfitMinusOperatingExpenses()
        {
            Figures(revenue: 1000m, cogs: 400m, operatingExpenses: 250m);

            var report = await ReportAsync();

            Assert.Equal(350m, report.Net_Profit);
        }

        [Fact]
        public async Task GetProfitAndLoss_NoOperatingExpenses_NetProfitEqualsGrossProfit()
        {
            Figures(revenue: 1000m, cogs: 400m, operatingExpenses: 0m);

            var report = await ReportAsync();

            Assert.Equal(report.Gross_Profit, report.Net_Profit);
        }

        [Fact]
        public async Task GetProfitAndLoss_ExpensesAboveGrossProfit_GivesNegativeNetProfit()
        {
            Figures(revenue: 1000m, cogs: 400m, operatingExpenses: 700m);

            var report = await ReportAsync();

            Assert.Equal(-100m, report.Net_Profit);
        }

        // ---- CalculateMargin ----

        [Fact]
        public async Task GetProfitAndLoss_GrossMarginIsGrossProfitOverRevenueTimes100()
        {
            Figures(revenue: 1000m, cogs: 400m, operatingExpenses: 250m);

            var report = await ReportAsync();

            // 600 / 1 000 x 100
            Assert.Equal(60m, report.Gross_Margin);
        }

        [Fact]
        public async Task GetProfitAndLoss_NetMarginIsNetProfitOverRevenueTimes100()
        {
            Figures(revenue: 1000m, cogs: 400m, operatingExpenses: 250m);

            var report = await ReportAsync();

            // 350 / 1 000 x 100
            Assert.Equal(35m, report.Net_Margin);
        }

        [Fact]
        public async Task GetProfitAndLoss_MarginIsRoundedToTwoDecimals()
        {
            Figures(revenue: 3m, cogs: 2m, operatingExpenses: 0m);

            var report = await ReportAsync();

            // 1 / 3 x 100 = 33.3333...
            Assert.Equal(33.33m, report.Gross_Margin);
        }

        [Fact]
        public async Task GetProfitAndLoss_MarginExactlyHalfwayRoundsAwayFromZero()
        {
            Figures(revenue: 800m, cogs: 799m, operatingExpenses: 0m);

            var report = await ReportAsync();

            // 1 / 800 x 100 = 0.125 exactly -> 0.13 (assumed rounding mode: away from zero)
            Assert.Equal(0.13m, report.Gross_Margin);
        }

        [Fact]
        public async Task GetProfitAndLoss_NegativeProfit_GivesNegativeMargin()
        {
            Figures(revenue: 1000m, cogs: 1500m, operatingExpenses: 0m);

            var report = await ReportAsync();

            Assert.Equal(-50m, report.Gross_Margin);
            Assert.Equal(-50m, report.Net_Margin);
        }

        [Fact]
        public async Task GetProfitAndLoss_NoCogs_GrossMarginIs100()
        {
            Figures(revenue: 500m, cogs: 0m, operatingExpenses: 0m);

            var report = await ReportAsync();

            Assert.Equal(100m, report.Gross_Margin);
        }

        [Fact]
        public async Task GetProfitAndLoss_ZeroRevenue_BothMarginsAreNull()
        {
            Figures(revenue: 0m, cogs: 400m, operatingExpenses: 250m);

            var report = await ReportAsync();

            Assert.Null(report.Gross_Margin);
            Assert.Null(report.Net_Margin);
            Assert.Equal(-400m, report.Gross_Profit);
            Assert.Equal(-650m, report.Net_Profit);
        }

        [Fact]
        public async Task GetProfitAndLoss_NoData_AllAmountsZeroAndMarginsNull()
        {
            var report = await ReportAsync();

            Assert.Equal(0m, report.Total_Revenue);
            Assert.Equal(0m, report.Total_Cogs);
            Assert.Equal(0m, report.Gross_Profit);
            Assert.Equal(0m, report.Total_Operating_Expenses);
            Assert.Equal(0m, report.Net_Profit);
            Assert.Null(report.Gross_Margin);
            Assert.Null(report.Net_Margin);
        }

        // ---- Period (Story 26 ResolvePeriod, unchanged) ----

        [Fact]
        public async Task GetProfitAndLoss_Month_ReturnsThePeriod()
        {
            var report = await ReportAsync("Month", new DateTime(2024, 3, 15));

            Assert.Equal("Month", report.Period_Type);
            Assert.Equal(new DateTime(2024, 3, 1), report.Start_Date);
            Assert.Equal(new DateTime(2024, 3, 31), report.End_Date);
        }

        [Fact]
        public async Task GetProfitAndLoss_Quarter_UsesTheCalendarQuarter()
        {
            var report = await ReportAsync("Quarter", new DateTime(2024, 5, 20));

            Assert.Equal(new DateTime(2024, 4, 1), report.Start_Date);
            Assert.Equal(new DateTime(2024, 6, 30), report.End_Date);
        }

        [Fact]
        public async Task GetProfitAndLoss_YearToDate_RunsFromJanuaryFirstToAnchor()
        {
            var report = await ReportAsync("YearToDate", new DateTime(2024, 3, 15));

            Assert.Equal(new DateTime(2024, 1, 1), report.Start_Date);
            Assert.Equal(new DateTime(2024, 3, 15), report.End_Date);
        }

        [Fact]
        public async Task GetProfitAndLoss_AllRepositoryReadsUseTheSamePeriod()
        {
            await ReportAsync("Quarter", new DateTime(2024, 5, 20));

            var expected = (new DateTime(2024, 4, 1), new DateTime(2024, 6, 30));
            Assert.Equal(expected, Assert.Single(_repository.SalesCalls));
            Assert.Equal(expected, Assert.Single(_repository.AdjustmentCalls));
            Assert.Equal(expected, Assert.Single(_repository.VehicleCalls));
            Assert.Equal(expected, Assert.Single(_repository.PayrollCalls));
            Assert.Equal(expected, Assert.Single(_repository.CandidateCalls));
        }

        [Theory]
        [InlineData("")]
        [InlineData("Week")]
        [InlineData("month")]
        public async Task GetProfitAndLoss_UnknownPeriodType_ThrowsAndReadsNothing(string type)
        {
            await Assert.ThrowsAsync<ValidationException>(() => ReportAsync(type));
            Assert.Empty(_repository.SalesCalls);
            Assert.Empty(_repository.VehicleCalls);
            Assert.Empty(_repository.PayrollCalls);
        }

        [Fact]
        public async Task GetProfitAndLoss_MissingAnchorDate_ThrowsValidationException()
        {
            await Assert.ThrowsAsync<ValidationException>(() =>
                CreateService().GetProfitAndLoss(new ReportPeriodDto { Period_Type = "Month", Anchor_Date = null }, "ann"));
            Assert.Empty(_repository.SalesCalls);
        }

        // ---- Generated_Date, Generated_By, AC3 ----

        [Fact]
        public async Task GetProfitAndLoss_GeneratedByIsThePassedUserName()
        {
            var report = await ReportAsync(generatedBy: "Neelesh Maharjan");

            Assert.Equal("Neelesh Maharjan", report.Generated_By);
        }

        [Fact]
        public async Task GetProfitAndLoss_GeneratedDateIsNow()
        {
            var before = DateTime.Now;

            var report = await ReportAsync();

            Assert.InRange(report.Generated_Date, before, DateTime.Now);
        }

        [Fact]
        public async Task GetProfitAndLoss_SamePeriodAndData_GiveTheSameFigures()
        {
            Figures(revenue: 30125m, cogs: 25200m, operatingExpenses: 3000m);

            var first = await ReportAsync();
            var second = await ReportAsync();

            Assert.Equal(first.Total_Revenue, second.Total_Revenue);
            Assert.Equal(first.Total_Cogs, second.Total_Cogs);
            Assert.Equal(first.Gross_Profit, second.Gross_Profit);
            Assert.Equal(first.Total_Operating_Expenses, second.Total_Operating_Expenses);
            Assert.Equal(first.Net_Profit, second.Net_Profit);
            Assert.Equal(first.Gross_Margin, second.Gross_Margin);
            Assert.Equal(first.Net_Margin, second.Net_Margin);
            Assert.Equal(first.Start_Date, second.Start_Date);
            Assert.Equal(first.End_Date, second.End_Date);
        }
    }
}
