using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.Models.Enums;
using NPMotors.Repository.Repositories;

namespace NPMotors.Repository.Tests.DataAccess
{
    public class VehicleRepositoryTest : TestBase, IAsyncLifetime
    {
        private readonly VehicleRepository _repository;
        private readonly VendorRepository _vendorRepository;
        private readonly string _marker = $"itest-{Guid.NewGuid():N}".Substring(0, 12);
        private readonly List<long> _vehicleIds = new();
        private readonly List<long> _vendorIds = new();

        public VehicleRepositoryTest()
        {
            _repository = new VehicleRepository(_repositoryContext);
            _vendorRepository = new VendorRepository(_repositoryContext);
        }

        public Task InitializeAsync() => Task.CompletedTask;

        public async Task DisposeAsync()
        {
            foreach (var id in _vehicleIds)
                await _repository.DeleteVehicle(id);
            foreach (var id in _vendorIds)
                await _vendorRepository.DeleteVendor(id);
        }

        private async Task<VendorResponseDto> CreateVendorAsync(Status status = Status.Active)
        {
            var vendor = await _vendorRepository.AddVendor(new VendorCreateDto
            {
                Name = $"{_marker} Vendor",
                Type = VendorType.Vehicles,
                Status = status
            });
            _vendorIds.Add(vendor.Id);
            return vendor;
        }

        private VehicleCreateDto NewDto(long vendorId, string suffix = "1") => new()
        {
            Vendor_Id = vendorId,
            Vin = $"{_marker}{suffix}".ToUpperInvariant().PadRight(17, '0').Substring(0, 17),
            Make = "Toyota",
            Model = "Camry",
            Year = 2022,
            Color = "Blue",
            Purchase_Date = new DateTime(2026, 1, 15),
            Warehouse = "Main",
            Purchase_Price = 20000m,
            Additional_Costs = 500.50m,
            Selling_Price = 25000m
        };

        [Fact]
        public async Task AddVehicle_CalculatesTotalCost_AndSetsAvailableStatus()
        {
            var vendor = await CreateVendorAsync();

            var created = await _repository.AddVehicle(NewDto(vendor.Id));
            _vehicleIds.Add(created.Id);

            Assert.True(created.Id > 0);
            Assert.Equal(vendor.Id, created.Vendor_Id);
            Assert.Equal(20500.50m, created.Total_Cost);
            Assert.Equal(VehicleStatus.Available, created.Status);
            Assert.Equal(new DateTime(2026, 1, 15), created.Purchase_Date);
        }

        [Fact]
        public async Task AddVehicle_DefaultsAdditionalCostsAndSellingPriceToZero()
        {
            var vendor = await CreateVendorAsync();
            var dto = NewDto(vendor.Id);
            dto.Additional_Costs = null;
            dto.Selling_Price = null;

            var created = await _repository.AddVehicle(dto);
            _vehicleIds.Add(created.Id);

            Assert.Equal(0m, created.Additional_Costs);
            Assert.Equal(0m, created.Selling_Price);
            Assert.Equal(created.Purchase_Price, created.Total_Cost);
        }

        [Fact]
        public async Task GetVendorStatus_ReturnsLowerCaseStatus_OrNullWhenMissing()
        {
            var inactive = await CreateVendorAsync(Status.Inactive);

            Assert.Equal("inactive", await _repository.GetVendorStatus(inactive.Id));
            Assert.Null(await _repository.GetVendorStatus(-1));
        }
    }
}
