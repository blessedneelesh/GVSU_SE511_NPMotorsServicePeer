using System.ComponentModel.DataAnnotations;
using NPMotors.Repository.Interface;
using NPMotors.Repository.Models.Dto;
using NPMotors.Services.Service;

namespace Module.Tests.Services
{
    // Story 27, GetCostOfGoodsSold. CalculateVehicleCogs is private static; it is tested only through GetCostOfGoodsSold.
    // ResolvePeriod is Story 26 and is covered by ReportServiceTest; here only its use is checked.
    public class ReportCogsServiceTest
    {
        // Hand-written fake: the test projects do not use a mocking library.
        private class FakeReportRepository : IReportRepository
        {
            public List<SoldVehicleCostDto> Vehicles { get; set; } = new();
            public List<(DateTime Start, DateTime End)> Calls { get; } = new();

            public Task<List<SoldVehicleCostDto>> GetSoldVehicleCosts(DateTime start, DateTime end)
            {
                Calls.Add((start, end));
                return Task.FromResult(Vehicles);
            }

            // Story 26 members: not used by these tests.
            public Task<SalesRevenueTotalsDto> GetSalesRevenue(DateTime start, DateTime end) => throw new NotSupportedException();
            public Task<List<IncomeAdjustmentDto>> GetIncomeAdjustments(DateTime start, DateTime end) => throw new NotSupportedException();

            // Story 28 members: not used by these tests.
            public Task<List<EmployeePayrollDto>> GetEmployeePayroll(DateTime start, DateTime end) => throw new NotSupportedException();
            public Task<List<ExpenseCandidateDto>> GetExpenseCandidates(DateTime start, DateTime end) => throw new NotSupportedException();
        }

        private readonly FakeReportRepository _repository = new();

        private ReportService CreateService() => new(_repository);

        private static SoldVehicleCostDto Vehicle(long id, decimal totalCost) => new()
        {
            Vehicle_Id = id, Vin = $"VIN{id:D14}", Invoice_Id = id * 10, Invoice_Number = $"INV-{id}",
            Invoice_Date = new DateTime(2024, 3, 10), Total_Cost = totalCost
        };

        private Task<CogsSummaryDto> CogsAsync(string type = "Month", DateTime? anchor = null) =>
            CreateService().GetCostOfGoodsSold(new ReportPeriodDto { Period_Type = type, Anchor_Date = anchor ?? new DateTime(2024, 3, 15) });

        // ---- CalculateVehicleCogs ----

        [Fact]
        public async Task GetCostOfGoodsSold_StoryExample_OneVehicleCost25200()
        {
            _repository.Vehicles = new() { Vehicle(1, 25200m) };

            var result = await CogsAsync();

            Assert.Equal(25200m, result.Vehicle_Cogs);
            Assert.Equal(0m, result.Parts_Cogs);
            Assert.Equal(25200m, result.Total_Cogs);
        }

        [Fact]
        public async Task GetCostOfGoodsSold_SeveralVehicles_SumsTheirTotalCost()
        {
            _repository.Vehicles = new() { Vehicle(1, 25200m), Vehicle(2, 18000m), Vehicle(3, 9500.50m) };

            var result = await CogsAsync();

            Assert.Equal(52700.50m, result.Vehicle_Cogs);
        }

        [Fact]
        public async Task GetCostOfGoodsSold_SameVehicleTwice_IsCountedOnce()
        {
            _repository.Vehicles = new() { Vehicle(1, 25200m), Vehicle(1, 25200m), Vehicle(2, 100m) };

            var result = await CogsAsync();

            Assert.Equal(25300m, result.Vehicle_Cogs);
        }

        [Fact]
        public async Task GetCostOfGoodsSold_EmptyList_VehicleCogsIsZero()
        {
            var result = await CogsAsync();

            Assert.Equal(0m, result.Vehicle_Cogs);
            Assert.Equal(0m, result.Total_Cogs);
        }

        [Fact]
        public async Task GetCostOfGoodsSold_ZeroCostVehicle_AddsNothingButIsCounted()
        {
            _repository.Vehicles = new() { Vehicle(1, 0m), Vehicle(2, 500m) };

            var result = await CogsAsync();

            Assert.Equal(500m, result.Vehicle_Cogs);
            Assert.Equal(2, result.Vehicles_Sold_Count);
        }

        [Fact]
        public async Task GetCostOfGoodsSold_KeepsDecimalCents()
        {
            _repository.Vehicles = new() { Vehicle(1, 0.10m), Vehicle(2, 0.20m) };

            var result = await CogsAsync();

            Assert.Equal(0.30m, result.Vehicle_Cogs);
        }

        [Fact]
        public async Task GetCostOfGoodsSold_UsesTotalCostOnly_NotVehicleIdOrInvoiceFields()
        {
            var vehicle = Vehicle(7, 1234m);
            vehicle.Invoice_Id = 999999;
            _repository.Vehicles = new() { vehicle };

            var result = await CogsAsync();

            Assert.Equal(1234m, result.Vehicle_Cogs);
        }

        // ---- Parts COGS (AC4) and Total_Cogs ----

        [Fact]
        public async Task GetCostOfGoodsSold_PartsCogsIsAlwaysZero()
        {
            _repository.Vehicles = new() { Vehicle(1, 25200m), Vehicle(2, 100m) };

            var result = await CogsAsync();

            Assert.Equal(0m, result.Parts_Cogs);
        }

        [Fact]
        public async Task GetCostOfGoodsSold_TotalIsVehiclePlusParts()
        {
            _repository.Vehicles = new() { Vehicle(1, 25200m), Vehicle(2, 100m) };

            var result = await CogsAsync();

            Assert.Equal(25300m, result.Total_Cogs);
        }

        // ---- Vehicles_Sold_Count ----

        [Fact]
        public async Task GetCostOfGoodsSold_SoldCountIsTheNumberOfVehicles()
        {
            _repository.Vehicles = new() { Vehicle(1, 10m), Vehicle(2, 20m), Vehicle(3, 30m) };

            var result = await CogsAsync();

            Assert.Equal(3, result.Vehicles_Sold_Count);
        }

        [Fact]
        public async Task GetCostOfGoodsSold_SoldCountIsDistinctVehicleId()
        {
            _repository.Vehicles = new() { Vehicle(1, 10m), Vehicle(1, 10m), Vehicle(2, 20m) };

            var result = await CogsAsync();

            Assert.Equal(2, result.Vehicles_Sold_Count);
        }

        [Fact]
        public async Task GetCostOfGoodsSold_NoVehicles_AllAmountsAndCountZero()
        {
            var result = await CogsAsync();

            Assert.Equal(0m, result.Vehicle_Cogs);
            Assert.Equal(0m, result.Parts_Cogs);
            Assert.Equal(0m, result.Total_Cogs);
            Assert.Equal(0, result.Vehicles_Sold_Count);
        }

        // ---- Period handling (Story 26 ResolvePeriod, unchanged) ----

        [Fact]
        public async Task GetCostOfGoodsSold_Month_PassesResolvedPeriodToRepositoryAndSummary()
        {
            var result = await CogsAsync("Month", new DateTime(2024, 3, 15));

            Assert.Equal((new DateTime(2024, 3, 1), new DateTime(2024, 3, 31)), Assert.Single(_repository.Calls));
            Assert.Equal("Month", result.Period_Type);
            Assert.Equal(new DateTime(2024, 3, 1), result.Start_Date);
            Assert.Equal(new DateTime(2024, 3, 31), result.End_Date);
        }

        [Fact]
        public async Task GetCostOfGoodsSold_Quarter_UsesTheCalendarQuarter()
        {
            var result = await CogsAsync("Quarter", new DateTime(2024, 5, 20));

            Assert.Equal(new DateTime(2024, 4, 1), result.Start_Date);
            Assert.Equal(new DateTime(2024, 6, 30), result.End_Date);
        }

        [Fact]
        public async Task GetCostOfGoodsSold_YearToDate_RunsFromJanuaryFirstToAnchor()
        {
            var result = await CogsAsync("YearToDate", new DateTime(2024, 3, 15));

            Assert.Equal((new DateTime(2024, 1, 1), new DateTime(2024, 3, 15)), Assert.Single(_repository.Calls));
        }

        [Theory]
        [InlineData("")]
        [InlineData("Week")]
        [InlineData("month")]
        public async Task GetCostOfGoodsSold_UnknownPeriodType_ThrowsAndDoesNotQuery(string type)
        {
            await Assert.ThrowsAsync<ValidationException>(() => CogsAsync(type));
            Assert.Empty(_repository.Calls);
        }

        [Fact]
        public async Task GetCostOfGoodsSold_MissingAnchorDate_ThrowsValidationException()
        {
            await Assert.ThrowsAsync<ValidationException>(() =>
                CreateService().GetCostOfGoodsSold(new ReportPeriodDto { Period_Type = "Month", Anchor_Date = null }));
            Assert.Empty(_repository.Calls);
        }
    }
}
