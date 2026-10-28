using System.Net;
using System.Text;
using Newtonsoft.Json;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.Repositories;

namespace Module.Tests.Controllers
{
    public class VendorControllerTest : TestBase, IAsyncLifetime
    {
        private VendorRepository _repo => new(_repositoryContext);
        private readonly string _marker = $"itest-{Guid.NewGuid():N}".Substring(0, 18);
        private readonly List<long> _createdIds = new();

        private class GetAllResponse
        {
            public List<VendorResponseDto> Items { get; set; } = new();
        }

        public Task InitializeAsync() => Task.CompletedTask;

        public async Task DisposeAsync()
        {
            foreach (var id in _createdIds)
            {
                try
                {
                    await _repo.DeleteVendor(id);
                }
                catch
                {
                    // Keep going so one failed delete does not block the rest.
                }
            }
        }

        private VendorCreateDto NewVendorDto(string suffix) => new()
        {
            Name = $"{_marker} Supply {suffix}",
            Contact = "Test Contact",
            Phone = "5550100",
            Email = $"{_marker}-{suffix}@example.com",
            Address = "1 Test St",
            Type = "Parts",
            Status = "active"
        };

        private async Task<VendorResponseDto> CreateVendorAsync(string suffix = "a", Action<VendorCreateDto>? customize = null)
        {
            var dto = NewVendorDto(suffix);
            customize?.Invoke(dto);
            var created = await _repo.AddVendor(dto);
            _createdIds.Add(created.Id);
            return created;
        }

        private static StringContent Json(object? body) =>
            new(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");

        private static async Task<T?> ReadAsync<T>(HttpResponseMessage response) =>
            JsonConvert.DeserializeObject<T>(await response.Content.ReadAsStringAsync());

        [Fact]
        public async Task GetAllVendors_Returns200_WithItemsAndMetaData()
        {
            // Arrange
            var created = await CreateVendorAsync();

            // Act
            var response = await _testClient.GetAsync($"Vendor/GetAllVendors?SearchTerm={_marker}");
            var json = await response.Content.ReadAsStringAsync();
            var body = JsonConvert.DeserializeObject<GetAllResponse>(json);

            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.NotNull(body);
            Assert.Contains(body!.Items, v => v.Id == created.Id);
            Assert.Contains("\"metaData\"", json);
        }

        [Fact]
        public async Task GetAllVendors_FiltersByStatusAndType()
        {
            // Arrange
            var activeParts = await CreateVendorAsync("activeparts");
            var inactiveVehicle = await CreateVendorAsync("inactivevehicle", d =>
            {
                d.Type = "Vehicle";
                d.Status = "inactive";
            });

            // Act
            var byStatus = await ReadAsync<GetAllResponse>(
                await _testClient.GetAsync($"Vendor/GetAllVendors?SearchTerm={_marker}&Status=inactive"));
            var byType = await ReadAsync<GetAllResponse>(
                await _testClient.GetAsync($"Vendor/GetAllVendors?SearchTerm={_marker}&Type=parts"));

            // Assert
            Assert.Single(byStatus!.Items);
            Assert.Equal(inactiveVehicle.Id, byStatus.Items[0].Id);
            Assert.Single(byType!.Items);
            Assert.Equal(activeParts.Id, byType.Items[0].Id);
        }

        [Fact]
        public async Task GetVendorById_Returns200_WithMatchingFields()
        {
            // Arrange
            var created = await CreateVendorAsync();

            // Act
            var response = await _testClient.GetAsync($"Vendor/GetVendorById?id={created.Id}");
            var body = await ReadAsync<VendorResponseDto>(response);

            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.NotNull(body);
            Assert.Equal(created.Id, body!.Id);
            Assert.Equal(created.Name, body.Name);
            Assert.Equal(created.Email, body.Email);
        }

        [Fact]
        public async Task GetVendorById_Returns404_WhenNotFound()
        {
            // Act
            var response = await _testClient.GetAsync("Vendor/GetVendorById?id=-1");

            // Assert
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        [Fact]
        public async Task GetActiveVendors_Returns200_WithoutInactiveVendors()
        {
            // Arrange
            var active = await CreateVendorAsync("active");
            var inactive = await CreateVendorAsync("inactive", d => d.Status = "inactive");

            // Act
            var response = await _testClient.GetAsync("Vendor/GetActiveVendors");
            var body = await ReadAsync<List<VendorResponseDto>>(response);

            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.NotNull(body);
            Assert.Contains(body!, v => v.Id == active.Id);
            Assert.DoesNotContain(body!, v => v.Id == inactive.Id);
        }

        [Fact]
        public async Task AddVendor_Returns201_AndPersistsVendorWithZeroBalances()
        {
            // Arrange
            var dto = NewVendorDto("post");
            dto.Type = "vehicle";

            // Act
            var response = await _testClient.PostAsync("Vendor/AddVendor", Json(dto));
            var body = await ReadAsync<VendorResponseDto>(response);
            if (body != null && body.Id > 0)
                _createdIds.Add(body.Id);

            // Assert
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            Assert.NotNull(body);
            Assert.True(body!.Id > 0);
            var reloaded = await _repo.GetVendorById(body.Id);
            Assert.NotNull(reloaded);
            Assert.Equal(dto.Name, reloaded!.Name);
            Assert.Equal("Vehicle", reloaded.Type);
            Assert.Equal(0m, reloaded.Balance);
            Assert.Equal(0m, reloaded.TotalPurchases);
        }

        [Fact]
        public async Task AddVendor_IgnoresBalanceAndTotalPurchasesInBody()
        {
            // Arrange
            var body = JsonConvert.DeserializeObject<Dictionary<string, object?>>(
                JsonConvert.SerializeObject(NewVendorDto("ignore")))!;
            body["balance"] = 999;
            body["totalPurchases"] = 999;

            // Act
            var response = await _testClient.PostAsync("Vendor/AddVendor", Json(body));
            var created = await ReadAsync<VendorResponseDto>(response);
            if (created != null && created.Id > 0)
                _createdIds.Add(created.Id);

            // Assert
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            Assert.Equal(0m, created!.Balance);
            Assert.Equal(0m, created.TotalPurchases);
        }

        [Fact]
        public async Task AddVendor_Returns400_WhenBodyIsNull()
        {
            // Act
            var response = await _testClient.PostAsync("Vendor/AddVendor", Json(null));

            // Assert
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task AddVendor_Returns400_WhenNameIsBlank()
        {
            // Arrange
            var blank = NewVendorDto("blank");
            blank.Name = "   ";

            // Act
            var response = await _testClient.PostAsync("Vendor/AddVendor", Json(blank));

            // Assert
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task AddVendor_Returns400_WhenTypeIsInvalid()
        {
            // Arrange
            var invalid = NewVendorDto("badtype");
            invalid.Type = "Hardware";

            // Act
            var response = await _testClient.PostAsync("Vendor/AddVendor", Json(invalid));

            // Assert
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task AddVendor_Returns400_WhenStatusIsInvalid()
        {
            // Arrange
            var invalid = NewVendorDto("badstatus");
            invalid.Status = "pending";

            // Act
            var response = await _testClient.PostAsync("Vendor/AddVendor", Json(invalid));

            // Assert
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task UpdateVendor_Returns200_AndPersistsChangesIncludingStatus()
        {
            // Arrange
            var created = await CreateVendorAsync();
            var update = new VendorUpdateDto
            {
                Name = $"{_marker} Updated",
                Contact = "New Contact",
                Phone = "5550199",
                Email = $"{_marker}-updated@example.com",
                Address = "99 Updated Ave",
                Type = "service",
                Status = "inactive"
            };

            // Act
            var response = await _testClient.PutAsync($"Vendor/UpdateVendor?id={created.Id}", Json(update));

            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var reloaded = await _repo.GetVendorById(created.Id);
            Assert.NotNull(reloaded);
            Assert.Equal(update.Name, reloaded!.Name);
            Assert.Equal("Service", reloaded.Type);
            Assert.Equal("inactive", reloaded.Status);
            Assert.Equal(0m, reloaded.Balance);
        }

        [Fact]
        public async Task UpdateVendor_Returns400_WhenTypeIsInvalid()
        {
            // Arrange
            var created = await CreateVendorAsync();
            var update = new VendorUpdateDto { Name = created.Name, Type = "Hardware", Status = "active" };

            // Act
            var response = await _testClient.PutAsync($"Vendor/UpdateVendor?id={created.Id}", Json(update));

            // Assert
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task UpdateVendor_Returns404_WhenNotFound()
        {
            // Arrange
            var update = new VendorUpdateDto { Name = $"{_marker} Ghost", Type = "Parts", Status = "active" };

            // Act
            var response = await _testClient.PutAsync("Vendor/UpdateVendor?id=-1", Json(update));

            // Assert
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        [Fact]
        public async Task UpdateVendor_Returns400_WhenBodyIsNull()
        {
            // Arrange
            var created = await CreateVendorAsync();

            // Act
            var response = await _testClient.PutAsync($"Vendor/UpdateVendor?id={created.Id}", Json(null));

            // Assert
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task DeleteVendor_Returns204_AndRemovesVendor()
        {
            // Arrange
            var created = await CreateVendorAsync();

            // Act
            var response = await _testClient.DeleteAsync($"Vendor/DeleteVendor?id={created.Id}");

            // Assert
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            Assert.Null(await _repo.GetVendorById(created.Id));
        }

        [Fact]
        public async Task DeleteVendor_Returns404_WhenNotFound()
        {
            // Act
            var response = await _testClient.DeleteAsync("Vendor/DeleteVendor?id=-1");

            // Assert
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }
}
