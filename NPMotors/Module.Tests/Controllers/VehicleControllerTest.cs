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

        private class GetAllResponse
        {
            public List<VehicleResponseDto> Items { get; set; } = new();
            public MetaDataResponse MetaData { get; set; } = new();
        }

        private class MetaDataResponse
        {
            public int CurrentPage { get; set; }
            public int TotalPages { get; set; }
            public int PageSize { get; set; }
            public int TotalCount { get; set; }
        }

        private async Task<VehicleResponseDto> CreateVehicleAsync(
            long vendorId, string suffix, string make = "Honda", string model = "Civic",
            string status = "available", decimal selling = 18000m)
        {
            var dto = NewDto(vendorId, suffix);
            dto.Make = make;
            dto.Model = model;
            dto.Selling_Price = selling;

            var created = await _vehicleRepo.AddVehicle(dto);
            _vehicleIds.Add(created.Id);

            if (status != "available")
            {
                using var connection = _repositoryContext.GetDatabaseConnection();
                await Dapper.SqlMapper.ExecuteAsync(connection,
                    "UPDATE vehicle SET status = @Status WHERE id = @Id",
                    new { Status = status, Id = created.Id });
            }
            return created;
        }

        private static async Task<T?> ReadAsync<T>(HttpResponseMessage response) =>
            JsonConvert.DeserializeObject<T>(await response.Content.ReadAsStringAsync());

        [Fact]
        public async Task GetAllVehicles_Returns200WithItemsAndMetaData()
        {
            var vendor = await CreateVendorAsync();
            var first = await CreateVehicleAsync(vendor.Id, "1");
            var second = await CreateVehicleAsync(vendor.Id, "2");

            var response = await _testClient.GetAsync($"Vehicle/GetAllVehicles?SearchTerm={_marker}");
            var body = await ReadAsync<GetAllResponse>(response);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.NotNull(body);
            Assert.Contains(body!.Items, v => v.Id == first.Id);
            Assert.Contains(body.Items, v => v.Id == second.Id);
            Assert.Equal(2, body.MetaData.TotalCount);
            Assert.All(body.Items, v => Assert.Equal(vendor.Name, v.Vendor_Name));
        }

        [Fact]
        public async Task GetAllVehicles_ItemsIncludeCalculatedFields()
        {
            var vendor = await CreateVendorAsync();
            await CreateVehicleAsync(vendor.Id, "1");

            var response = await _testClient.GetAsync($"Vehicle/GetAllVehicles?SearchTerm={_marker}");
            var body = await ReadAsync<GetAllResponse>(response);

            var item = Assert.Single(body!.Items);
            Assert.Equal(15250m, item.Total_Cost);
            Assert.Equal(2750m, item.Gross_Profit);
            Assert.Equal(15.2778m, item.Gross_Margin!.Value, 4);
            Assert.True(item.Is_Available);
        }

        [Fact]
        public async Task GetAllVehicles_BindsPagingParameters()
        {
            var vendor = await CreateVendorAsync();
            await CreateVehicleAsync(vendor.Id, "1");
            await CreateVehicleAsync(vendor.Id, "2");
            await CreateVehicleAsync(vendor.Id, "3");

            var response = await _testClient.GetAsync(
                $"Vehicle/GetAllVehicles?SearchTerm={_marker}&PageNumber=2&PageSize=2");
            var body = await ReadAsync<GetAllResponse>(response);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Single(body!.Items);
            Assert.Equal(2, body.MetaData.CurrentPage);
            Assert.Equal(2, body.MetaData.PageSize);
            Assert.Equal(3, body.MetaData.TotalCount);
            Assert.Equal(2, body.MetaData.TotalPages);
        }

        [Fact]
        public async Task GetAllVehicles_BindsStatusAndSearchTermTogether()
        {
            var vendor = await CreateVendorAsync();
            var token = Guid.NewGuid().ToString("N").Substring(0, 8);
            var hit = await CreateVehicleAsync(vendor.Id, "1", $"Mk{token}", "Civic", "reserved");
            await CreateVehicleAsync(vendor.Id, "2", $"Mk{token}", "Civic");
            await CreateVehicleAsync(vendor.Id, "3", "Ford", "Focus", "reserved");

            var response = await _testClient.GetAsync(
                $"Vehicle/GetAllVehicles?SearchTerm=MK{token}&Status=reserved");
            var body = await ReadAsync<GetAllResponse>(response);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var only = Assert.Single(body!.Items);
            Assert.Equal(hit.Id, only.Id);
            Assert.Equal(VehicleStatus.Reserved, only.Status);
            Assert.False(only.Is_Available);
        }

        [Fact]
        public async Task GetAllVehicles_PageSizeAbove50_IsCappedAt50()
        {
            var response = await _testClient.GetAsync("Vehicle/GetAllVehicles?PageSize=500");
            var body = await ReadAsync<GetAllResponse>(response);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(50, body!.MetaData.PageSize);
        }

        [Theory]
        [InlineData("sold")]
        [InlineData("bogus")]
        public async Task GetAllVehicles_UnsupportedStatus_Returns400(string status)
        {
            var response = await _testClient.GetAsync($"Vehicle/GetAllVehicles?Status={status}");

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task GetVehicleById_Existing_Returns200WithCalculatedFields()
        {
            var vendor = await CreateVendorAsync();
            var created = await CreateVehicleAsync(vendor.Id, "1");

            var response = await _testClient.GetAsync($"Vehicle/GetVehicleById?id={created.Id}");
            var body = await ReadAsync<VehicleResponseDto>(response);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(created.Id, body!.Id);
            Assert.Equal(vendor.Name, body.Vendor_Name);
            Assert.Equal(15250m, body.Total_Cost);
            Assert.Equal(2750m, body.Gross_Profit);
            Assert.Equal(15.2778m, body.Gross_Margin!.Value, 4);
            Assert.True(body.Is_Available);
        }

        [Fact]
        public async Task GetVehicleById_SellingPriceZero_ReturnsNullMargin()
        {
            var vendor = await CreateVendorAsync();
            var created = await CreateVehicleAsync(vendor.Id, "1", selling: 0m);

            var response = await _testClient.GetAsync($"Vehicle/GetVehicleById?id={created.Id}");
            var body = await ReadAsync<VehicleResponseDto>(response);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Null(body!.Gross_Margin);
            Assert.Equal(-15250m, body.Gross_Profit);
        }

        [Fact]
        public async Task GetVehicleById_Unknown_Returns404()
        {
            var response = await _testClient.GetAsync("Vehicle/GetVehicleById?id=-1");

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        [Fact]
        public async Task GetVehicleById_NonNumericId_Returns400()
        {
            var response = await _testClient.GetAsync("Vehicle/GetVehicleById?id=abc");

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Theory]
        [InlineData("PUT", "Vehicle/UpdateVehicle?id=1")]
        [InlineData("DELETE", "Vehicle/DeleteVehicle?id=1")]
        public async Task UpdateAndDeleteEndpoints_AreNotMapped(string method, string url)
        {
            var response = await _testClient.SendAsync(new HttpRequestMessage(new HttpMethod(method), url));

            Assert.Contains(response.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
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

        [Theory]
        [InlineData("1234567890123456")]
        [InlineData("123456789012345678")]
        public async Task AddVehicle_InvalidVinLength_Returns400(string vin)
        {
            var vendor = await CreateVendorAsync();
            var dto = NewDto(vendor.Id);
            dto.Vin = vin;

            var response = await PostAsync(dto);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task AddVehicle_DuplicateVin_Returns400()
        {
            var vendor = await CreateVendorAsync();
            var dto = NewDto(vendor.Id);

            var firstResponse = await PostAsync(dto);
            var secondResponse = await PostAsync(dto);

            Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, secondResponse.StatusCode);
        }
    }
}
