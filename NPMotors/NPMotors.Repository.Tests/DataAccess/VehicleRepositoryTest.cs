using System.Data;
using Dapper;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.RequestFeatures;
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

        private async Task<VehicleResponseDto> CreateVehicleAsync(
            long vendorId, string suffix, string make, string model,
            string status = "available", decimal purchase = 20000m, decimal additional = 500m, decimal selling = 25000m)
        {
            var dto = NewDto(vendorId, suffix);
            dto.Make = make;
            dto.Model = model;
            dto.Purchase_Price = purchase;
            dto.Additional_Costs = additional;
            dto.Selling_Price = selling;

            var created = await _repository.AddVehicle(dto);
            _vehicleIds.Add(created.Id);

            if (status != "available")
            {
                using IDbConnection connection = _repositoryContext.GetDatabaseConnection();
                await connection.ExecuteAsync("UPDATE vehicle SET status = @Status WHERE id = @Id",
                    new { Status = status, Id = created.Id });
            }
            return created;
        }

        // The marker is part of every test VIN, so searching for it isolates this test's rows.
        private VehicleParameters Scoped(string? status = null, string? search = null) => new()
        {
            Status = status,
            SearchTerm = search ?? _marker
        };

        [Fact]
        public async Task GetAllVehicles_NoStatus_ReturnsAllStatuses()
        {
            var vendor = await CreateVendorAsync();
            var available = await CreateVehicleAsync(vendor.Id, "1", "Toyota", "Camry");
            var reserved = await CreateVehicleAsync(vendor.Id, "2", "Toyota", "Corolla", "reserved");

            var result = await _repository.GetAllVehicles(Scoped());

            Assert.Equal(2, result.MetaData.TotalCount);
            Assert.Contains(result, v => v.Id == available.Id);
            Assert.Contains(result, v => v.Id == reserved.Id);
        }

        [Theory]
        [InlineData("available")]
        [InlineData("reserved")]
        public async Task GetAllVehicles_StatusFilter_ReturnsOnlyThatStatus(string status)
        {
            var vendor = await CreateVendorAsync();
            var available = await CreateVehicleAsync(vendor.Id, "1", "Toyota", "Camry");
            var reserved = await CreateVehicleAsync(vendor.Id, "2", "Toyota", "Corolla", "reserved");

            var result = await _repository.GetAllVehicles(Scoped(status));

            var expected = status == "available" ? available : reserved;
            var only = Assert.Single(result);
            Assert.Equal(expected.Id, only.Id);
            Assert.Equal(1, result.MetaData.TotalCount);
        }

        [Fact]
        public async Task GetAllVehicles_SearchTerm_MatchesVinPartialAndCaseInsensitive()
        {
            var vendor = await CreateVendorAsync();
            var vehicle = await CreateVehicleAsync(vendor.Id, "1", "Toyota", "Camry");
            var mixedCase = "ItEsT-" + _marker.Substring(6);

            var result = await _repository.GetAllVehicles(Scoped(search: mixedCase));

            Assert.Contains(result, v => v.Id == vehicle.Id);
        }

        [Fact]
        public async Task GetAllVehicles_SearchTerm_MatchesMake()
        {
            var vendor = await CreateVendorAsync();
            var token = Guid.NewGuid().ToString("N").Substring(0, 8);
            var match = await CreateVehicleAsync(vendor.Id, "1", $"Mk{token}", "Camry");
            await CreateVehicleAsync(vendor.Id, "2", "Other", "Camry");

            var result = await _repository.GetAllVehicles(Scoped(search: token.Substring(1, 5).ToUpperInvariant()));

            var only = Assert.Single(result);
            Assert.Equal(match.Id, only.Id);
        }

        [Fact]
        public async Task GetAllVehicles_SearchTerm_MatchesModel()
        {
            var vendor = await CreateVendorAsync();
            var token = Guid.NewGuid().ToString("N").Substring(0, 8);
            var match = await CreateVehicleAsync(vendor.Id, "1", "Toyota", $"Md{token}");
            await CreateVehicleAsync(vendor.Id, "2", "Toyota", "Other");

            var result = await _repository.GetAllVehicles(Scoped(search: token.Substring(2, 5).ToUpperInvariant()));

            var only = Assert.Single(result);
            Assert.Equal(match.Id, only.Id);
        }

        [Fact]
        public async Task GetAllVehicles_StatusAndSearch_BothApply()
        {
            var vendor = await CreateVendorAsync();
            var token = Guid.NewGuid().ToString("N").Substring(0, 8);
            var hit = await CreateVehicleAsync(vendor.Id, "1", $"Mk{token}", "Camry", "reserved");
            await CreateVehicleAsync(vendor.Id, "2", $"Mk{token}", "Corolla");          // matches search, wrong status
            await CreateVehicleAsync(vendor.Id, "3", "Other", "Camry", "reserved");     // matches status, wrong search

            var result = await _repository.GetAllVehicles(Scoped("reserved", $"mk{token}"));

            var only = Assert.Single(result);
            Assert.Equal(hit.Id, only.Id);
            Assert.Equal(1, result.MetaData.TotalCount);
        }

        [Fact]
        public async Task GetAllVehicles_OrderedByMakeThenModel()
        {
            var vendor = await CreateVendorAsync();
            var token = Guid.NewGuid().ToString("N").Substring(0, 8).ToUpperInvariant();
            var aZ = await CreateVehicleAsync(vendor.Id, "1", $"{token}A", "Z");
            var bA = await CreateVehicleAsync(vendor.Id, "2", $"{token}B", "A");
            var aC = await CreateVehicleAsync(vendor.Id, "3", $"{token}A", "C");

            var result = await _repository.GetAllVehicles(Scoped());

            Assert.Equal(new[] { aC.Id, aZ.Id, bA.Id }, result.Select(v => v.Id).ToArray());
        }

        [Fact]
        public async Task GetAllVehicles_Paging_SecondPageAndTotalCountOfAllMatches()
        {
            var vendor = await CreateVendorAsync();
            var token = Guid.NewGuid().ToString("N").Substring(0, 8).ToUpperInvariant();
            var first = await CreateVehicleAsync(vendor.Id, "1", $"{token}A", "A");
            var second = await CreateVehicleAsync(vendor.Id, "2", $"{token}B", "A");
            var third = await CreateVehicleAsync(vendor.Id, "3", $"{token}C", "A");

            var page2 = await _repository.GetAllVehicles(
                new VehicleParameters { SearchTerm = _marker, PageNumber = 2, PageSize = 1 });

            var only = Assert.Single(page2);
            Assert.Equal(second.Id, only.Id);
            Assert.Equal(3, page2.MetaData.TotalCount);
            Assert.Equal(3, page2.MetaData.TotalPages);
            Assert.Equal(2, page2.MetaData.CurrentPage);
            Assert.Equal(1, page2.MetaData.PageSize);
        }

        [Fact]
        public async Task GetAllVehicles_NoMatches_ReturnsEmptyPagedList()
        {
            var result = await _repository.GetAllVehicles(
                new VehicleParameters { SearchTerm = $"no-such-vehicle-{Guid.NewGuid():N}" });

            Assert.NotNull(result);
            Assert.Empty(result);
            Assert.Equal(0, result.MetaData.TotalCount);
        }

        [Fact]
        public async Task GetAllVehicles_IncludesVendorName()
        {
            var vendor = await CreateVendorAsync();
            await CreateVehicleAsync(vendor.Id, "1", "Toyota", "Camry");

            var result = await _repository.GetAllVehicles(Scoped());

            var only = Assert.Single(result);
            Assert.Equal(vendor.Name, only.Vendor_Name);
            Assert.Equal(vendor.Id, only.Vendor_Id);
        }

        [Fact]
        public async Task GetAllVehicles_CalculatedFieldsAreLeftAtDefaults()
        {
            var vendor = await CreateVendorAsync();
            await CreateVehicleAsync(vendor.Id, "1", "Toyota", "Camry");

            var only = Assert.Single(await _repository.GetAllVehicles(Scoped()));

            Assert.Equal(20000m, only.Purchase_Price);
            Assert.Equal(500m, only.Additional_Costs);
            Assert.Equal(25000m, only.Selling_Price);
            Assert.Equal(0m, only.Total_Cost);
            Assert.Equal(0m, only.Gross_Profit);
            Assert.Null(only.Gross_Margin);
            Assert.False(only.Is_Available);
        }

        [Fact]
        public async Task GetVehicleById_Existing_ReturnsVehicleWithVendorName()
        {
            var vendor = await CreateVendorAsync();
            var created = await CreateVehicleAsync(vendor.Id, "1", "Toyota", "Camry", "reserved");

            var result = await _repository.GetVehicleById(created.Id);

            Assert.NotNull(result);
            Assert.Equal(created.Id, result!.Id);
            Assert.Equal(created.Vin, result.Vin);
            Assert.Equal(vendor.Name, result.Vendor_Name);
            Assert.Equal(VehicleStatus.Reserved, result.Status);
            Assert.Equal(0m, result.Gross_Profit);
        }

        [Fact]
        public async Task GetVehicleById_Unknown_ReturnsNull()
        {
            Assert.Null(await _repository.GetVehicleById(-1));
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
