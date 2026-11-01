using NPMotors.Repository.Interface;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.Models.Enums;
using NPMotors.Repository.RequestFeatures;
using NPMotors.Services.Service;

namespace Module.Tests.Services
{
    public class VehicleServiceTest
    {
        // Hand-written fake: the test projects do not use a mocking library.
        private class FakeVehicleRepository : IVehicleRepository
        {
            public PagedList<VehicleResponseDto> GetAllResult { get; set; } =
                new(new List<VehicleResponseDto>(), 0, 1, 5);
            public VehicleResponseDto? GetByIdResult { get; set; }

            public List<VehicleParameters> GetAllCalls { get; } = new();
            public List<long> GetByIdCalls { get; } = new();

            public Task<PagedList<VehicleResponseDto>> GetAllVehicles(VehicleParameters parameters)
            {
                GetAllCalls.Add(parameters);
                return Task.FromResult(GetAllResult);
            }

            public Task<VehicleResponseDto?> GetVehicleById(long id)
            {
                GetByIdCalls.Add(id);
                return Task.FromResult(GetByIdResult);
            }

            public Task<VehicleResponseDto> AddVehicle(VehicleCreateDto vehicle) => throw new NotSupportedException();
            public Task<string?> GetVendorStatus(long vendorId) => throw new NotSupportedException();
            public Task<bool> DeleteVehicle(long id) => throw new NotSupportedException();
        }

        private readonly FakeVehicleRepository _repository = new();
        private readonly VehicleService _service;

        public VehicleServiceTest()
        {
            _service = new VehicleService(_repository);
        }

        private static VehicleResponseDto Vehicle(
            decimal purchase, decimal additional, decimal selling,
            VehicleStatus status = VehicleStatus.Available, long id = 1) => new()
        {
            Id = id,
            Vendor_Id = 7,
            Vendor_Name = "Test Vendor",
            Vin = "VIN" + id,
            Purchase_Price = purchase,
            Additional_Costs = additional,
            Selling_Price = selling,
            Status = status
        };

        private async Task<VehicleResponseDto> CalculatedAsync(VehicleResponseDto vehicle)
        {
            _repository.GetByIdResult = vehicle;
            var result = await _service.GetVehicleById(vehicle.Id);
            Assert.NotNull(result);
            return result!;
        }

        // ---- CalculateProfit via GetVehicleById ----

        [Fact]
        public async Task GetVehicleById_CalculatesTotalCost()
        {
            var result = await CalculatedAsync(Vehicle(10000m, 1000m, 14000m));

            Assert.Equal(11000m, result.Total_Cost);
        }

        [Fact]
        public async Task GetVehicleById_GrossProfitUsesTotalCostNotPurchasePrice()
        {
            var result = await CalculatedAsync(Vehicle(10000m, 1000m, 14000m));

            Assert.Equal(3000m, result.Gross_Profit);
            Assert.NotEqual(4000m, result.Gross_Profit);
        }

        [Fact]
        public async Task GetVehicleById_CalculatesGrossMargin()
        {
            var result = await CalculatedAsync(Vehicle(10000m, 1000m, 14000m));

            // 3000 / 14000 * 100 = 21.428571...
            Assert.NotNull(result.Gross_Margin);
            Assert.Equal(21.4285714286m, result.Gross_Margin!.Value, 8);
        }

        [Fact]
        public async Task GetVehicleById_AdditionalCostsChangeProfit()
        {
            var without = await CalculatedAsync(Vehicle(10000m, 0m, 14000m));
            var with = await CalculatedAsync(Vehicle(10000m, 1000m, 14000m));

            Assert.Equal(4000m, without.Gross_Profit);
            Assert.Equal(3000m, with.Gross_Profit);
        }

        [Fact]
        public async Task GetVehicleById_ZeroAdditionalCosts_TotalCostEqualsPurchasePrice()
        {
            var result = await CalculatedAsync(Vehicle(8000m, 0m, 10000m));

            Assert.Equal(8000m, result.Total_Cost);
            Assert.Equal(2000m, result.Gross_Profit);
            Assert.Equal(20m, result.Gross_Margin);
        }

        [Fact]
        public async Task GetVehicleById_SellingPriceZero_MarginNullAndProfitIsNegativeTotalCost()
        {
            var result = await CalculatedAsync(Vehicle(10000m, 500m, 0m));

            Assert.Null(result.Gross_Margin);
            Assert.Equal(-10500m, result.Gross_Profit);
        }

        [Fact]
        public async Task GetVehicleById_SellingBelowCost_NegativeProfitAndMarginNotClamped()
        {
            var result = await CalculatedAsync(Vehicle(10000m, 3000m, 12000m));

            Assert.Equal(13000m, result.Total_Cost);
            Assert.Equal(-1000m, result.Gross_Profit);
            // -1000 / 12000 * 100 = -8.3333...
            Assert.Equal(-8.3333333333m, result.Gross_Margin!.Value, 8);
        }

        [Fact]
        public async Task GetVehicleById_SellingEqualsCost_ZeroProfitZeroMargin()
        {
            var result = await CalculatedAsync(Vehicle(9000m, 1000m, 10000m));

            Assert.Equal(0m, result.Gross_Profit);
            Assert.Equal(0m, result.Gross_Margin);
        }

        [Fact]
        public async Task GetVehicleById_StatusAvailable_IsAvailableTrue()
        {
            var result = await CalculatedAsync(Vehicle(1000m, 0m, 2000m, VehicleStatus.Available));

            Assert.True(result.Is_Available);
            Assert.Equal(VehicleStatus.Available, result.Status);
        }

        [Fact]
        public async Task GetVehicleById_StatusReserved_IsAvailableFalse()
        {
            var result = await CalculatedAsync(Vehicle(1000m, 0m, 2000m, VehicleStatus.Reserved));

            Assert.False(result.Is_Available);
            Assert.Equal(VehicleStatus.Reserved, result.Status);
        }

        [Fact]
        public async Task GetVehicleById_StatusSold_IsAvailableFalse()
        {
            var result = await CalculatedAsync(Vehicle(1000m, 0m, 2000m, VehicleStatus.Sold));

            Assert.False(result.Is_Available);
        }

        // ---- GetVehicleById ----

        [Fact]
        public async Task GetVehicleById_NotFound_ReturnsNull()
        {
            _repository.GetByIdResult = null;

            var result = await _service.GetVehicleById(99);

            Assert.Null(result);
        }

        // ---- GetAllVehicles ----

        [Fact]
        public async Task GetAllVehicles_CalculatesEveryItem()
        {
            _repository.GetAllResult = new PagedList<VehicleResponseDto>(new List<VehicleResponseDto>
            {
                Vehicle(10000m, 1000m, 14000m, VehicleStatus.Available, 1),
                Vehicle(20000m, 0m, 20000m, VehicleStatus.Reserved, 2),
                Vehicle(5000m, 500m, 0m, VehicleStatus.Available, 3)
            }, 3, 1, 5);

            var result = await _service.GetAllVehicles(new VehicleParameters());

            Assert.Equal(3, result.Count);

            Assert.Equal(11000m, result[0].Total_Cost);
            Assert.Equal(3000m, result[0].Gross_Profit);
            Assert.Equal(21.4285714286m, result[0].Gross_Margin!.Value, 8);
            Assert.True(result[0].Is_Available);

            Assert.Equal(20000m, result[1].Total_Cost);
            Assert.Equal(0m, result[1].Gross_Profit);
            Assert.Equal(0m, result[1].Gross_Margin);
            Assert.False(result[1].Is_Available);

            Assert.Equal(5500m, result[2].Total_Cost);
            Assert.Equal(-5500m, result[2].Gross_Profit);
            Assert.Null(result[2].Gross_Margin);
            Assert.True(result[2].Is_Available);
        }

        [Fact]
        public async Task GetAllVehicles_KeepsMetaData()
        {
            _repository.GetAllResult = new PagedList<VehicleResponseDto>(new List<VehicleResponseDto>
            {
                Vehicle(1000m, 0m, 2000m, id: 1)
            }, 12, 2, 5);

            var result = await _service.GetAllVehicles(new VehicleParameters { PageNumber = 2, PageSize = 5 });

            Assert.Equal(12, result.MetaData.TotalCount);
            Assert.Equal(2, result.MetaData.CurrentPage);
            Assert.Equal(5, result.MetaData.PageSize);
            Assert.Equal(3, result.MetaData.TotalPages);
        }

        // ---- Status validation ----

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("available")]
        [InlineData("reserved")]
        [InlineData("Available")]
        [InlineData("RESERVED")]
        public async Task GetAllVehicles_AllowedStatus_CallsRepository(string? status)
        {
            await _service.GetAllVehicles(new VehicleParameters { Status = status });

            Assert.Single(_repository.GetAllCalls);
        }

        [Theory]
        [InlineData("sold")]
        [InlineData("bogus")]
        public async Task GetAllVehicles_UnsupportedStatus_ThrowsAndSkipsRepository(string status)
        {
            await Assert.ThrowsAsync<ArgumentException>(
                () => _service.GetAllVehicles(new VehicleParameters { Status = status }));

            Assert.Empty(_repository.GetAllCalls);
        }

        // ---- Page size cap ----

        [Fact]
        public async Task GetAllVehicles_PageSizeAboveCap_IsCappedAt50()
        {
            var parameters = new VehicleParameters { PageSize = 500 };

            await _service.GetAllVehicles(parameters);

            Assert.Equal(50, Assert.Single(_repository.GetAllCalls).PageSize);
        }
    }
}
