using System.ComponentModel.DataAnnotations;
using NPMotors.Repository.Interface;
using NPMotors.Repository.Models.Dto;
using NPMotors.Services.Service;

namespace Module.Tests.Services
{
    // Story 30, ComparePeriods. CalculatePercentageChange and EnsureNoOverlap are private static; they are tested only
    // through ComparePeriods. Each period's figures are served by a fake repository keyed by the period's start date.
    public class ReportComparePeriodsServiceTest
    {
        private class PeriodData
        {
            public SalesRevenueTotalsDto Sales { get; } = new();
            public List<SoldVehicleCostDto> Vehicles { get; } = new();
            public List<EmployeePayrollDto> Employees { get; } = new();
        }

        // Hand-written fake: the test projects do not use a mocking library.
        private class FakeReportRepository : IReportRepository
        {
            public Dictionary<DateTime, PeriodData> Periods { get; } = new();
            public List<(DateTime Start, DateTime End)> SalesCalls { get; } = new();

            private PeriodData Data(DateTime start) => Periods.TryGetValue(start, out var data) ? data : new PeriodData();

            public Task<SalesRevenueTotalsDto> GetSalesRevenue(DateTime start, DateTime end)
            {
                SalesCalls.Add((start, end));
                return Task.FromResult(Data(start).Sales);
            }

            public Task<List<SoldVehicleCostDto>> GetSoldVehicleCosts(DateTime start, DateTime end) => Task.FromResult(Data(start).Vehicles);
            public Task<List<EmployeePayrollDto>> GetEmployeePayroll(DateTime start, DateTime end) => Task.FromResult(Data(start).Employees);
            public Task<List<IncomeAdjustmentDto>> GetIncomeAdjustments(DateTime start, DateTime end) => Task.FromResult(new List<IncomeAdjustmentDto>());
            public Task<List<ExpenseCandidateDto>> GetExpenseCandidates(DateTime start, DateTime end) => Task.FromResult(new List<ExpenseCandidateDto>());
        }

        private readonly FakeReportRepository _repository = new();

        private ReportService CreateService() => new(_repository);

        // Figures for the period that starts on `start`: revenue (vehicle revenue only), COGS (one vehicle's cost) and,
        // for Month periods, operating expenses (one employee whose annual salary / 12 equals the amount).
        private void Period(DateTime start, decimal revenue, decimal cogs, decimal operatingExpenses = 0m)
        {
            var data = new PeriodData();
            _repository.Periods[start] = data;
            data.Sales.Vehicle_Revenue = revenue;
            data.Vehicles.Add(new SoldVehicleCostDto { Vehicle_Id = 1, Total_Cost = cogs });
            data.Employees.Add(new EmployeePayrollDto { Employee_Id = 1, Salary = operatingExpenses * 12m });
        }

        private static readonly DateTime March = new(2024, 3, 1);
        private static readonly DateTime February = new(2024, 2, 1);

        // Current = March 2024, previous = February 2024 (the default previous month).
        private void MarchVersusFebruary(decimal currentRevenue, decimal currentCogs, decimal previousRevenue, decimal previousCogs,
            decimal currentOpex = 0m, decimal previousOpex = 0m)
        {
            Period(March, currentRevenue, currentCogs, currentOpex);
            Period(February, previousRevenue, previousCogs, previousOpex);
        }

        private Task<PeriodComparisonDto> CompareAsync(string type = "Month", DateTime? current = null, DateTime? previous = null,
            string generatedBy = "ann") =>
            CreateService().ComparePeriods(new ComparePeriodsDto
            {
                Period_Type = type, Current_Anchor_Date = current ?? new DateTime(2024, 3, 15), Previous_Anchor_Date = previous
            }, generatedBy);

        // ---- Story example (AC1-AC3 formulas) ----

        [Fact]
        public async Task ComparePeriods_StoryExample_ReturnsTheDocumentedChanges()
        {
            // Previous: revenue 25 000, COGS 20 000, opex 2 500 -> gross 5 000 (20.00 %), net 2 500 (10.00 %).
            // Current:  revenue 30 125, COGS 25 200, opex 3 000 -> gross 4 925 (16.35 %), net 1 925 (6.39 %).
            MarchVersusFebruary(30125m, 25200m, 25000m, 20000m, currentOpex: 3000m, previousOpex: 2500m);

            var result = await CompareAsync();

            Assert.Equal(20.50m, result.Revenue_Change_Pct);
            Assert.Equal(26.00m, result.Cogs_Change_Pct);
            Assert.Equal(-1.50m, result.Gross_Profit_Change_Pct);
            Assert.Equal(20.00m, result.Operating_Expense_Change_Pct);
            Assert.Equal(-23.00m, result.Net_Profit_Change_Pct);
            Assert.Equal(-3.65m, result.Gross_Margin_Change);
            Assert.Equal(-3.61m, result.Net_Margin_Change);
        }

        [Fact]
        public async Task ComparePeriods_ReturnsBothFullReports()
        {
            MarchVersusFebruary(30125m, 25200m, 25000m, 20000m, currentOpex: 3000m, previousOpex: 2500m);

            var result = await CompareAsync();

            Assert.Equal(30125m, result.Current.Total_Revenue);
            Assert.Equal(1925m, result.Current.Net_Profit);
            Assert.Equal(25000m, result.Previous.Total_Revenue);
            Assert.Equal(2500m, result.Previous.Net_Profit);
        }

        // ---- CalculatePercentageChange (AC2, AC3) ----

        [Fact]
        public async Task ComparePeriods_Growth_IsPositive()
        {
            MarchVersusFebruary(1500m, 0m, 1000m, 0m);

            var result = await CompareAsync();

            Assert.Equal(50m, result.Revenue_Change_Pct);
        }

        [Fact]
        public async Task ComparePeriods_Decline_IsNegative()
        {
            MarchVersusFebruary(750m, 0m, 1000m, 0m);

            var result = await CompareAsync();

            Assert.Equal(-25m, result.Revenue_Change_Pct);
        }

        [Fact]
        public async Task ComparePeriods_NoChange_IsZero()
        {
            MarchVersusFebruary(1000m, 400m, 1000m, 400m);

            var result = await CompareAsync();

            Assert.Equal(0m, result.Revenue_Change_Pct);
            Assert.Equal(0m, result.Cogs_Change_Pct);
            Assert.Equal(0m, result.Gross_Profit_Change_Pct);
        }

        [Fact]
        public async Task ComparePeriods_CurrentZero_IsMinus100()
        {
            MarchVersusFebruary(0m, 0m, 1000m, 0m);

            var result = await CompareAsync();

            Assert.Equal(-100m, result.Revenue_Change_Pct);
        }

        [Fact]
        public async Task ComparePeriods_PreviousZero_ChangeIsUnavailable()
        {
            MarchVersusFebruary(1000m, 400m, 0m, 0m);

            var result = await CompareAsync();

            Assert.Null(result.Revenue_Change_Pct);
            Assert.Null(result.Cogs_Change_Pct);
            Assert.Null(result.Gross_Profit_Change_Pct);
            Assert.Null(result.Operating_Expense_Change_Pct);
            Assert.Null(result.Net_Profit_Change_Pct);
        }

        [Fact]
        public async Task ComparePeriods_PreviousPeriodWithNoData_AllChangesUnavailable()
        {
            Period(March, 1000m, 400m, 100m);

            var result = await CompareAsync();

            Assert.Null(result.Revenue_Change_Pct);
            Assert.Null(result.Cogs_Change_Pct);
            Assert.Null(result.Gross_Profit_Change_Pct);
            Assert.Null(result.Operating_Expense_Change_Pct);
            Assert.Null(result.Net_Profit_Change_Pct);
            Assert.Null(result.Gross_Margin_Change);
            Assert.Null(result.Net_Margin_Change);
        }

        [Fact]
        public async Task ComparePeriods_UnavailableIsPerAmount_OnlyTheZeroPreviousAmountIsNull()
        {
            // Previous revenue is 0 but previous COGS is 400.
            MarchVersusFebruary(1000m, 600m, 0m, 400m);

            var result = await CompareAsync();

            Assert.Null(result.Revenue_Change_Pct);
            Assert.Equal(50m, result.Cogs_Change_Pct);
        }

        [Fact]
        public async Task ComparePeriods_NegativePreviousProfit_UsesTheAbsolutePreviousSoImprovementIsPositive()
        {
            // Previous: revenue 1 000, COGS 2 000 -> gross and net profit -1 000. Current: revenue 1 500, COGS 1 000 -> 500.
            MarchVersusFebruary(1500m, 1000m, 1000m, 2000m);

            var result = await CompareAsync();

            // (500 - (-1 000)) / |-1 000| x 100 = +150
            Assert.Equal(150m, result.Gross_Profit_Change_Pct);
            Assert.Equal(150m, result.Net_Profit_Change_Pct);
        }

        [Fact]
        public async Task ComparePeriods_ProfitTurningNegative_ShowsANegativeChange()
        {
            // Previous profit 1 000, current profit -500.
            MarchVersusFebruary(500m, 1000m, 2000m, 1000m);

            var result = await CompareAsync();

            // (-500 - 1 000) / 1 000 x 100 = -150
            Assert.Equal(-150m, result.Net_Profit_Change_Pct);
        }

        [Fact]
        public async Task ComparePeriods_LossGettingDeeper_ShowsANegativeChange()
        {
            // Previous profit -1 000, current profit -1 500.
            MarchVersusFebruary(500m, 2000m, 1000m, 2000m);

            var result = await CompareAsync();

            // (-1 500 - (-1 000)) / |-1 000| x 100 = -50
            Assert.Equal(-50m, result.Net_Profit_Change_Pct);
        }

        [Fact]
        public async Task ComparePeriods_NegativeProfitIsReturnedAsANegativeAmount()
        {
            MarchVersusFebruary(500m, 2000m, 1000m, 2000m);

            var result = await CompareAsync();

            Assert.Equal(-1500m, result.Current.Net_Profit);
            Assert.Equal(-1000m, result.Previous.Net_Profit);
            Assert.Equal(-300m, result.Current.Net_Margin);
        }

        [Fact]
        public async Task ComparePeriods_ChangeIsRoundedToTwoDecimals()
        {
            MarchVersusFebruary(4m, 0m, 3m, 0m);

            var result = await CompareAsync();

            // 1 / 3 x 100 = 33.3333...
            Assert.Equal(33.33m, result.Revenue_Change_Pct);
        }

        [Fact]
        public async Task ComparePeriods_ChangeExactlyHalfwayRoundsAwayFromZero()
        {
            MarchVersusFebruary(801m, 0m, 800m, 0m);

            var result = await CompareAsync();

            // 1 / 800 x 100 = 0.125 exactly -> 0.13 (assumed rounding mode: away from zero)
            Assert.Equal(0.13m, result.Revenue_Change_Pct);
        }

        [Fact]
        public async Task ComparePeriods_OperatingExpenseChangeUsesOperatingExpensesNotNetProfit()
        {
            MarchVersusFebruary(10000m, 0m, 10000m, 0m, currentOpex: 300m, previousOpex: 200m);

            var result = await CompareAsync();

            Assert.Equal(50m, result.Operating_Expense_Change_Pct);
        }

        // ---- Margin changes (percentage points) ----

        [Fact]
        public async Task ComparePeriods_MarginChangeIsInPercentagePointsNotPercent()
        {
            // Gross margin 60 % now, 40 % before.
            MarchVersusFebruary(1000m, 400m, 1000m, 600m);

            var result = await CompareAsync();

            Assert.Equal(20m, result.Gross_Margin_Change);
        }

        [Fact]
        public async Task ComparePeriods_MarginFalling_GivesANegativeChange()
        {
            MarchVersusFebruary(1000m, 600m, 1000m, 400m);

            var result = await CompareAsync();

            Assert.Equal(-20m, result.Gross_Margin_Change);
        }

        [Fact]
        public async Task ComparePeriods_MarginChangeIsNullWhenEitherRevenueIsZero()
        {
            MarchVersusFebruary(0m, 0m, 1000m, 400m);
            var currentZero = await CompareAsync();

            MarchVersusFebruary(1000m, 400m, 0m, 0m);
            var previousZero = await CompareAsync();

            Assert.Null(currentZero.Gross_Margin_Change);
            Assert.Null(currentZero.Net_Margin_Change);
            Assert.Null(previousZero.Gross_Margin_Change);
            Assert.Null(previousZero.Net_Margin_Change);
        }

        // ---- Previous anchor default (step 1) ----

        [Fact]
        public async Task ComparePeriods_MonthWithoutPreviousAnchor_ComparesWithThePreviousMonth()
        {
            var result = await CompareAsync("Month", new DateTime(2024, 3, 15));

            Assert.Equal(new DateTime(2024, 3, 1), result.Current.Start_Date);
            Assert.Equal(new DateTime(2024, 2, 1), result.Previous.Start_Date);
            Assert.Equal(new DateTime(2024, 2, 29), result.Previous.End_Date);
        }

        [Fact]
        public async Task ComparePeriods_MonthInJanuary_ComparesWithDecemberOfThePreviousYear()
        {
            var result = await CompareAsync("Month", new DateTime(2024, 1, 15));

            Assert.Equal(new DateTime(2023, 12, 1), result.Previous.Start_Date);
            Assert.Equal(new DateTime(2023, 12, 31), result.Previous.End_Date);
        }

        [Fact]
        public async Task ComparePeriods_MonthAnchoredOnThe31st_ComparesWithTheShorterPreviousMonth()
        {
            var result = await CompareAsync("Month", new DateTime(2024, 3, 31));

            Assert.Equal(new DateTime(2024, 2, 1), result.Previous.Start_Date);
            Assert.Equal(new DateTime(2024, 2, 29), result.Previous.End_Date);
        }

        [Fact]
        public async Task ComparePeriods_QuarterWithoutPreviousAnchor_ComparesWithThePreviousQuarter()
        {
            var result = await CompareAsync("Quarter", new DateTime(2024, 5, 20));

            Assert.Equal(new DateTime(2024, 4, 1), result.Current.Start_Date);
            Assert.Equal(new DateTime(2024, 6, 30), result.Current.End_Date);
            Assert.Equal(new DateTime(2024, 1, 1), result.Previous.Start_Date);
            Assert.Equal(new DateTime(2024, 3, 31), result.Previous.End_Date);
        }

        [Fact]
        public async Task ComparePeriods_YearToDateWithoutPreviousAnchor_ComparesWithTheSameDatePreviousYear()
        {
            var result = await CompareAsync("YearToDate", new DateTime(2024, 3, 15));

            Assert.Equal(new DateTime(2024, 1, 1), result.Current.Start_Date);
            Assert.Equal(new DateTime(2024, 3, 15), result.Current.End_Date);
            Assert.Equal(new DateTime(2023, 1, 1), result.Previous.Start_Date);
            Assert.Equal(new DateTime(2023, 3, 15), result.Previous.End_Date);
        }

        [Fact]
        public async Task ComparePeriods_PreviousAnchorGiven_IsUsedInsteadOfTheDefault()
        {
            Period(new DateTime(2023, 11, 1), 2000m, 0m);
            Period(March, 3000m, 0m);

            var result = await CompareAsync("Month", new DateTime(2024, 3, 15), new DateTime(2023, 11, 10));

            Assert.Equal(new DateTime(2023, 11, 1), result.Previous.Start_Date);
            Assert.Equal(2000m, result.Previous.Total_Revenue);
            Assert.Equal(50m, result.Revenue_Change_Pct);
        }

        // ---- Same rules for both periods (AC1) ----

        [Fact]
        public async Task ComparePeriods_BothReportsUseTheSamePeriodType()
        {
            var result = await CompareAsync("Quarter", new DateTime(2024, 5, 20));

            Assert.Equal("Quarter", result.Current.Period_Type);
            Assert.Equal("Quarter", result.Previous.Period_Type);
        }

        [Fact]
        public async Task ComparePeriods_BuildsEachReportFromItsOwnPeriod()
        {
            await CompareAsync("Month", new DateTime(2024, 3, 15));

            Assert.Contains((new DateTime(2024, 3, 1), new DateTime(2024, 3, 31)), _repository.SalesCalls);
            Assert.Contains((new DateTime(2024, 2, 1), new DateTime(2024, 2, 29)), _repository.SalesCalls);
            Assert.Equal(2, _repository.SalesCalls.Count);
        }

        [Fact]
        public async Task ComparePeriods_PassesGeneratedByToBothReports()
        {
            var result = await CompareAsync(generatedBy: "Neelesh Maharjan");

            Assert.Equal("Neelesh Maharjan", result.Current.Generated_By);
            Assert.Equal("Neelesh Maharjan", result.Previous.Generated_By);
        }

        // ---- EnsureNoOverlap (AC1) ----

        [Fact]
        public async Task ComparePeriods_PreviousAnchorInsideTheCurrentMonth_ThrowsAndBuildsNoReport()
        {
            await Assert.ThrowsAsync<ValidationException>(() =>
                CompareAsync("Month", new DateTime(2024, 3, 15), new DateTime(2024, 3, 5)));
            Assert.Empty(_repository.SalesCalls);
        }

        [Fact]
        public async Task ComparePeriods_PreviousAnchorOnTheFirstDayOfTheCurrentPeriod_Overlaps()
        {
            await Assert.ThrowsAsync<ValidationException>(() =>
                CompareAsync("Month", new DateTime(2024, 3, 15), new DateTime(2024, 3, 1)));
        }

        [Fact]
        public async Task ComparePeriods_PreviousPeriodAfterTheCurrentOne_Throws()
        {
            await Assert.ThrowsAsync<ValidationException>(() =>
                CompareAsync("Month", new DateTime(2024, 3, 15), new DateTime(2024, 4, 10)));
        }

        [Fact]
        public async Task ComparePeriods_SamePeriodTwice_Throws()
        {
            await Assert.ThrowsAsync<ValidationException>(() =>
                CompareAsync("Quarter", new DateTime(2024, 5, 20), new DateTime(2024, 5, 20)));
        }

        [Fact]
        public async Task ComparePeriods_PreviousQuarterAnchorInTheSameQuarter_Overlaps()
        {
            await Assert.ThrowsAsync<ValidationException>(() =>
                CompareAsync("Quarter", new DateTime(2024, 5, 20), new DateTime(2024, 6, 30)));
        }

        [Fact]
        public async Task ComparePeriods_PreviousPeriodEndingTheDayBeforeTheCurrentOne_IsAllowed()
        {
            var result = await CompareAsync("Month", new DateTime(2024, 3, 15), new DateTime(2024, 2, 29));

            Assert.Equal(new DateTime(2024, 2, 29), result.Previous.End_Date);
        }

        [Fact]
        public async Task ComparePeriods_YearToDatePreviousYearEnd_IsAllowed()
        {
            var result = await CompareAsync("YearToDate", new DateTime(2024, 3, 15), new DateTime(2023, 12, 31));

            Assert.Equal(new DateTime(2023, 1, 1), result.Previous.Start_Date);
            Assert.Equal(new DateTime(2023, 12, 31), result.Previous.End_Date);
        }

        [Fact]
        public async Task ComparePeriods_YearToDateOnJanuaryFirstOfTheCurrentYear_Overlaps()
        {
            await Assert.ThrowsAsync<ValidationException>(() =>
                CompareAsync("YearToDate", new DateTime(2024, 3, 15), new DateTime(2024, 1, 1)));
        }

        // ---- Validation ----

        [Theory]
        [InlineData("")]
        [InlineData("Week")]
        [InlineData("month")]
        public async Task ComparePeriods_UnknownPeriodType_ThrowsAndBuildsNoReport(string type)
        {
            await Assert.ThrowsAsync<ValidationException>(() => CompareAsync(type));
            Assert.Empty(_repository.SalesCalls);
        }

        [Fact]
        public async Task ComparePeriods_UnknownPeriodTypeWithAPreviousAnchor_Throws()
        {
            await Assert.ThrowsAsync<ValidationException>(() => CompareAsync("Week", new DateTime(2024, 3, 15), new DateTime(2024, 1, 10)));
            Assert.Empty(_repository.SalesCalls);
        }

        [Fact]
        public async Task ComparePeriods_MissingCurrentAnchorDate_ThrowsValidationException()
        {
            await Assert.ThrowsAsync<ValidationException>(() =>
                CreateService().ComparePeriods(new ComparePeriodsDto { Period_Type = "Month", Current_Anchor_Date = null }, "ann"));
            Assert.Empty(_repository.SalesCalls);
        }
    }
}
