using System.Net;
using System.Text;
using Newtonsoft.Json;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.Models.Enums;
using NPMotors.Repository.Repositories;

namespace Module.Tests.Controllers
{
    public class VehicleControllerTest : TestBase, IAsyncLifetime
    {
        private VendorRepository _vendorRepo => new(_repositoryContext);
        private VehicleRepository _vehicleRepo => new(_repositoryContext);
        private readonly string _marker = $"itest-{Guid.NewGuid():N}".Substring(0, 12);
        private readonly List<long> _vehicleIds = new();
        private readonly List<long> _vendorIds = new();

        public Task InitializeAsync() => Task.CompletedTask;

        public async Task DisposeAsync()
        {
            foreach (var id in _vehicleIds)
            {
                try { await _vehicleRepo.DeleteVehicle(id); } catch { }
            }
            foreach (var id in _vendorIds)
            {
                try { await _vendorRepo.DeleteVendor(id); } catch { }
            }
        }

        private async Task<VendorResponseDto> CreateVendorAsync(Status status = Status.Active)
        {
            var vendor = await _vendorRepo.AddVendor(new VendorCreateDto
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
            Make = "Honda",
            Model = "Civic",
            Year = 2021,
            Purchase_Price = 15000m,
            Additional_Costs = 250m,
            Selling_Price = 18000m
        };

        private static StringContent Json(object? body) =>
            new(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");

        private async Task<HttpResponseMessage> PostAsync(VehicleCreateDto dto)
        {
            var response = await _testClient.PostAsync("Vehicle/AddVehicle", Json(dto));
            if (response.StatusCode == HttpStatusCode.Created)
            {
                var created = JsonConvert.DeserializeObject<VehicleResponseDto>(
                    await response.Content.ReadAsStringAsync());
                _vehicleIds.Add(created!.Id);
            }
            return response;
        }

        [Fact]
        public async Task AddVehicle_Returns201_WithTotalCostAndAvailableStatus()
        {
            var vendor = await CreateVendorAsync();

            var response = await PostAsync(NewDto(vendor.Id));
            var body = JsonConvert.DeserializeObject<VehicleResponseDto>(await response.Content.ReadAsStringAsync());

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            Assert.Equal(15250m, body!.Total_Cost);
            Assert.Equal(VehicleStatus.Available, body.Status);
        }

        [Fact]
        public async Task AddVehicle_InactiveVendor_Returns400()
        {
            var vendor = await CreateVendorAsync(Status.Inactive);

            var response = await PostAsync(NewDto(vendor.Id));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task AddVehicle_UnknownVendor_Returns400()
        {
            var response = await PostAsync(NewDto(-1));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Theory]
        [InlineData(0, 0, 0)]
        [InlineData(-5, 0, 0)]
        [InlineData(1000, -1, 0)]
        [InlineData(1000, 0, -1)]
        public async Task AddVehicle_InvalidAmounts_Returns400(decimal purchase, decimal additional, decimal selling)
        {
            var vendor = await CreateVendorAsync();
            var dto = NewDto(vendor.Id);
            dto.Purchase_Price = purchase;
            dto.Additional_Costs = additional;
            dto.Selling_Price = selling;

            var response = await PostAsync(dto);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task AddVehicle_MissingVin_Returns400()
        {
            var vendor = await CreateVendorAsync();
            var dto = NewDto(vendor.Id);
            dto.Vin = "";

            var response = await PostAsync(dto);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }
}
