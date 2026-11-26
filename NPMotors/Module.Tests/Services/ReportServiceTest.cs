using System.ComponentModel.DataAnnotations;
using NPMotors.Repository.Interface;
using NPMotors.Repository.Models.Dto;
using NPMotors.Services.Service;

namespace Module.Tests.Services
{
    // Story 26, GetRevenue. ResolvePeriod, SplitAdjustmentIncome and CalculateTotalRevenue are private static;
    // they are tested only through GetRevenue.
    public class ReportServiceTest
    {
        // Hand-written fake: the test projects do not use a mocking library.
        private class FakeReportRepository : IReportRepository
        {
            public SalesRevenueTotalsDto Sales { get; set; } = new();
            public List<IncomeAdjustmentDto> Adjustments { get; set; } = new();
            public List<(DateTime Start, DateTime End)> SalesCalls { get; } = new();
            public List<(DateTime Start, DateTime End)> AdjustmentCalls { get; } = new();

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

            // Story 27 member: not used by the Story 26 tests.
            public Task<List<SoldVehicleCostDto>> GetSoldVehicleCosts(DateTime start, DateTime end) =>
                throw new NotSupportedException();
        }

        private readonly FakeReportRepository _repository = new();

        private ReportService CreateService() => new(_repository);

        private static ReportPeriodDto Period(string type, DateTime anchor) =>
            new() { Period_Type = type, Anchor_Date = anchor };

        private static IncomeAdjustmentDto Adjustment(string type, decimal amount) =>
            new() { Transaction_Id = 1, Number = "TXN-1", Date = new DateTime(2024, 3, 10), Amount = amount, Adjustment_Type = type };

        private Task<RevenueSummaryDto> RevenueAsync(string type, DateTime anchor) =>
            CreateService().GetRevenue(Period(type, anchor));

        // ---- ResolvePeriod ----

        [Fact]
        public async Task GetRevenue_Month_CoversFirstToLastDayOfAnchorMonth()
        {
            var result = await RevenueAsync("Month", new DateTime(2024, 3, 15));

            Assert.Equal(new DateTime(2024, 3, 1), result.Start_Date);
            Assert.Equal(new DateTime(2024, 3, 31), result.End_Date);
        }

        [Fact]
        public async Task GetRevenue_MonthInLeapFebruary_EndsOnThe29th()
        {
            var result = await RevenueAsync("Month", new DateTime(2024, 2, 10));

            Assert.Equal(new DateTime(2024, 2, 1), result.Start_Date);
            Assert.Equal(new DateTime(2024, 2, 29), result.End_Date);
        }

        [Fact]
        public async Task GetRevenue_MonthInNonLeapFebruary_EndsOnThe28th()
        {
            var result = await RevenueAsync("Month", new DateTime(2023, 2, 10));

            Assert.Equal(new DateTime(2023, 2, 28), result.End_Date);
        }

        [Fact]
        public async Task GetRevenue_MonthAnchoredOnLastDay_StillStartsOnFirst()
        {
            var result = await RevenueAsync("Month", new DateTime(2024, 12, 31));

            Assert.Equal(new DateTime(2024, 12, 1), result.Start_Date);
            Assert.Equal(new DateTime(2024, 12, 31), result.End_Date);
        }

        [Fact]
        public async Task GetRevenue_QuarterInSecondQuarter_CoversAprilToJune()
        {
            var result = await RevenueAsync("Quarter", new DateTime(2024, 5, 20));

            Assert.Equal(new DateTime(2024, 4, 1), result.Start_Date);
            Assert.Equal(new DateTime(2024, 6, 30), result.End_Date);
        }

        [Fact]
        public async Task GetRevenue_QuarterInFirstQuarter_CoversJanuaryToMarch()
        {
            var result = await RevenueAsync("Quarter", new DateTime(2024, 2, 10));

            Assert.Equal(new DateTime(2024, 1, 1), result.Start_Date);
            Assert.Equal(new DateTime(2024, 3, 31), result.End_Date);
        }

        [Fact]
        public async Task GetRevenue_QuarterInThirdQuarter_CoversJulyToSeptember()
        {
            var result = await RevenueAsync("Quarter", new DateTime(2024, 9, 30));

            Assert.Equal(new DateTime(2024, 7, 1), result.Start_Date);
            Assert.Equal(new DateTime(2024, 9, 30), result.End_Date);
        }

        [Fact]
        public async Task GetRevenue_QuarterInFourthQuarter_CoversOctoberToDecember()
        {
            var result = await RevenueAsync("Quarter", new DateTime(2024, 12, 31));

            Assert.Equal(new DateTime(2024, 10, 1), result.Start_Date);
            Assert.Equal(new DateTime(2024, 12, 31), result.End_Date);
        }

        [Fact]
        public async Task GetRevenue_YearToDate_RunsFromJanuaryFirstToAnchor()
        {
            var result = await RevenueAsync("YearToDate", new DateTime(2024, 3, 15));

            Assert.Equal(new DateTime(2024, 1, 1), result.Start_Date);
            Assert.Equal(new DateTime(2024, 3, 15), result.End_Date);
        }

        [Fact]
        public async Task GetRevenue_YearToDateAnchoredOnJanuaryFirst_IsASingleDay()
        {
            var result = await RevenueAsync("YearToDate", new DateTime(2024, 1, 1));

            Assert.Equal(new DateTime(2024, 1, 1), result.Start_Date);
            Assert.Equal(new DateTime(2024, 1, 1), result.End_Date);
        }

        [Fact]
        public async Task GetRevenue_AnchorWithTimeOfDay_UsesDatesOnly()
        {
            var result = await RevenueAsync("YearToDate", new DateTime(2024, 3, 15, 17, 45, 0));

            Assert.Equal(new DateTime(2024, 3, 15), result.End_Date);
        }

        [Fact]
        public async Task GetRevenue_PassesTheResolvedPeriodToBothRepositoryMethods()
        {
            await RevenueAsync("Quarter", new DateTime(2024, 5, 20));

            Assert.Equal((new DateTime(2024, 4, 1), new DateTime(2024, 6, 30)), Assert.Single(_repository.SalesCalls));
            Assert.Equal((new DateTime(2024, 4, 1), new DateTime(2024, 6, 30)), Assert.Single(_repository.AdjustmentCalls));
        }

        [Fact]
        public async Task GetRevenue_EchoesThePeriodType()
        {
            var result = await RevenueAsync("Quarter", new DateTime(2024, 5, 20));

            Assert.Equal("Quarter", result.Period_Type);
        }

        [Theory]
        [InlineData("")]
        [InlineData("Week")]
        [InlineData("month")]
        public async Task GetRevenue_UnknownPeriodType_ThrowsValidationException(string type)
        {
            await Assert.ThrowsAsync<ValidationException>(() => RevenueAsync(type, new DateTime(2024, 3, 15)));
            Assert.Empty(_repository.SalesCalls);
        }

        [Fact]
        public async Task GetRevenue_MissingAnchorDate_ThrowsValidationException()
        {
            await Assert.ThrowsAsync<ValidationException>(() =>
                CreateService().GetRevenue(new ReportPeriodDto { Period_Type = "Month", Anchor_Date = null }));
            Assert.Empty(_repository.SalesCalls);
        }

        // ---- SplitAdjustmentIncome ----

        [Fact]
        public async Task GetRevenue_InterestReceivedAdjustments_AreSummedIntoInterestIncome()
        {
            _repository.Adjustments = new() { Adjustment("Interest Received", 400m), Adjustment("Interest Received", 225m) };

            var result = await RevenueAsync("Month", new DateTime(2024, 3, 15));

            Assert.Equal(625m, result.Interest_Income);
            Assert.Equal(0m, result.Other_Income);
        }

        [Fact]
        public async Task GetRevenue_OtherAdjustments_AreSummedIntoOtherIncome()
        {
            _repository.Adjustments = new() { Adjustment("Other Adjustment", 100m), Adjustment("Other Adjustment", 50m) };

            var result = await RevenueAsync("Month", new DateTime(2024, 3, 15));

            Assert.Equal(150m, result.Other_Income);
            Assert.Equal(0m, result.Interest_Income);
        }

        [Fact]
        public async Task GetRevenue_OtherIncomingTypes_AreIgnored()
        {
            _repository.Adjustments = new()
            {
                Adjustment("Vendor Refund", 999m),
                Adjustment("Interest Received", 625m),
                Adjustment("Bank Fee", 10m)
            };

            var result = await RevenueAsync("Month", new DateTime(2024, 3, 15));

            Assert.Equal(625m, result.Interest_Income);
            Assert.Equal(0m, result.Other_Income);
            Assert.Equal(625m, result.Total_Revenue);
        }

        [Fact]
        public async Task GetRevenue_AdjustmentTypeMatchIsCaseSensitive()
        {
            _repository.Adjustments = new() { Adjustment("interest received", 625m) };

            var result = await RevenueAsync("Month", new DateTime(2024, 3, 15));

            Assert.Equal(0m, result.Interest_Income);
        }

        [Fact]
        public async Task GetRevenue_NoAdjustments_InterestAndOtherAreZero()
        {
            var result = await RevenueAsync("Month", new DateTime(2024, 3, 15));

            Assert.Equal(0m, result.Interest_Income);
            Assert.Equal(0m, result.Other_Income);
        }

        [Fact]
        public async Task GetRevenue_KeepsDecimalCents()
        {
            _repository.Adjustments = new() { Adjustment("Interest Received", 0.10m), Adjustment("Interest Received", 0.20m) };

            var result = await RevenueAsync("Month", new DateTime(2024, 3, 15));

            Assert.Equal(0.30m, result.Interest_Income);
        }

        // ---- CalculateTotalRevenue ----

        [Fact]
        public async Task GetRevenue_StoryExample_InvoiceAndInterestTotal30125()
        {
            // SalePrice 30 000 + Fees 500 - Discount 1 000 = 29 500; + interest received 625 = 30 125.
            _repository.Sales = new SalesRevenueTotalsDto
            {
                Vehicle_Revenue = 30000m, Fee_Income = 500m, Discounts = 1000m, Invoice_Count = 1
            };
            _repository.Adjustments = new() { Adjustment("Interest Received", 625m) };

            var result = await RevenueAsync("Month", new DateTime(2024, 3, 15));

            Assert.Equal(30125m, result.Total_Revenue);
        }

        [Fact]
        public async Task GetRevenue_TotalAddsAllFiveIncomesAndSubtractsDiscounts()
        {
            _repository.Sales = new SalesRevenueTotalsDto
            {
                Vehicle_Revenue = 1000m, Service_Revenue = 200m, Fee_Income = 30m, Discounts = 4m, Invoice_Count = 2
            };
            _repository.Adjustments = new() { Adjustment("Interest Received", 5m), Adjustment("Other Adjustment", 60m) };

            var result = await RevenueAsync("Month", new DateTime(2024, 3, 15));

            // 1000 + 200 + 30 + 5 + 60 - 4 = 1291
            Assert.Equal(1291m, result.Total_Revenue);
        }

        [Fact]
        public async Task GetRevenue_ServiceRevenueIsIncludedInTheTotal()
        {
            _repository.Sales = new SalesRevenueTotalsDto { Service_Revenue = 200m };

            var result = await RevenueAsync("Month", new DateTime(2024, 3, 15));

            Assert.Equal(200m, result.Total_Revenue);
        }

        [Fact]
        public async Task GetRevenue_DiscountsLargerThanIncome_GivesNegativeTotal()
        {
            _repository.Sales = new SalesRevenueTotalsDto { Vehicle_Revenue = 100m, Discounts = 150m };

            var result = await RevenueAsync("Month", new DateTime(2024, 3, 15));

            Assert.Equal(-50m, result.Total_Revenue);
        }

        [Fact]
        public async Task GetRevenue_NoRecords_AllAmountsZero()
        {
            var result = await RevenueAsync("Month", new DateTime(2024, 3, 15));

            Assert.Equal(0m, result.Vehicle_Revenue);
            Assert.Equal(0m, result.Service_Revenue);
            Assert.Equal(0m, result.Fee_Income);
            Assert.Equal(0m, result.Interest_Income);
            Assert.Equal(0m, result.Other_Income);
            Assert.Equal(0m, result.Discounts);
            Assert.Equal(0m, result.Total_Revenue);
            Assert.Equal(0, result.Invoice_Count);
        }

        [Fact]
        public async Task GetRevenue_CopiesSalesAmountsAndInvoiceCountToTheSummary()
        {
            _repository.Sales = new SalesRevenueTotalsDto
            {
                Vehicle_Revenue = 1000m, Service_Revenue = 200m, Fee_Income = 30m, Discounts = 4m, Invoice_Count = 7
            };

            var result = await RevenueAsync("Month", new DateTime(2024, 3, 15));

            Assert.Equal(1000m, result.Vehicle_Revenue);
            Assert.Equal(200m, result.Service_Revenue);
            Assert.Equal(30m, result.Fee_Income);
            Assert.Equal(4m, result.Discounts);
            Assert.Equal(7, result.Invoice_Count);
        }
    }
}
