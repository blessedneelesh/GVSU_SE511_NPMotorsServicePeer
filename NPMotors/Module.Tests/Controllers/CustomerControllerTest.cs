using System.Net;
using System.Text;
using Newtonsoft.Json;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.Repositories;

namespace Module.Tests.Controllers
{
    public class CustomerControllerTest : TestBase, IAsyncLifetime
    {
        private CustomerRepository _repo => new(_repositoryContext);
        private readonly string _marker = $"itest-{Guid.NewGuid():N}".Substring(0, 18);
        private readonly List<long> _createdIds = new();

        private class GetAllResponse
        {
            public List<CustomerResponseDto> Items { get; set; } = new();
        }

        public Task InitializeAsync() => Task.CompletedTask;

        public async Task DisposeAsync()
        {
            foreach (var id in _createdIds)
            {
                try
                {
                    await _repo.DeleteCustomer(id);
                }
                catch
                {
                    // Keep going so one failed delete does not block the rest.
                }
            }
        }

        private CustomerCreateDto NewCustomerDto(string suffix) => new()
        {
            First_Name = $"Test{suffix}",
            Last_Name = _marker,
            Phone = "5550100",
            Email = $"{_marker}-{suffix}@example.com",
            Address = "1 Test St",
            City = "Allendale",
            State = "MI",
            Zip = "49401",
            Status = "active"
        };

        private async Task<CustomerResponseDto> CreateCustomerAsync(string suffix = "a")
        {
            var created = await _repo.AddCustomer(NewCustomerDto(suffix));
            _createdIds.Add(created.Id);
            return created;
        }

        private static StringContent Json(object? body) =>
            new(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");

        private static async Task<T?> ReadAsync<T>(HttpResponseMessage response) =>
            JsonConvert.DeserializeObject<T>(await response.Content.ReadAsStringAsync());

        [Fact]
        public async Task GetAllCustomers_Returns200_AndContainsInsertedCustomer()
        {
            // Arrange
            var created = await CreateCustomerAsync();

            // Act
            var response = await _testClient.GetAsync($"Customer/GetAllCustomers?SearchTerm={_marker}");
            var body = await ReadAsync<GetAllResponse>(response);

            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.NotNull(body);
            Assert.Contains(body!.Items, c => c.Id == created.Id);
        }

        [Fact]
        public async Task GetCustomerById_Returns200_WithMatchingFields()
        {
            // Arrange
            var created = await CreateCustomerAsync();

            // Act
            var response = await _testClient.GetAsync($"Customer/GetCustomerById?id={created.Id}");
            var body = await ReadAsync<CustomerResponseDto>(response);

            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.NotNull(body);
            Assert.Equal(created.Id, body!.Id);
            Assert.Equal(created.Email, body.Email);
            Assert.Equal(created.First_Name, body.First_Name);
        }

        [Fact]
        public async Task GetCustomerById_Returns404_WhenNotFound()
        {
            // Act
            var response = await _testClient.GetAsync("Customer/GetCustomerById?id=-1");

            // Assert
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        [Fact]
        public async Task AddCustomer_Returns201_AndPersistsCustomer()
        {
            // Arrange
            var dto = NewCustomerDto("post");

            // Act
            var response = await _testClient.PostAsync("Customer/AddCustomer", Json(dto));
            var body = await ReadAsync<CustomerResponseDto>(response);
            if (body != null && body.Id > 0)
                _createdIds.Add(body.Id);

            // Assert
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            Assert.NotNull(body);
            Assert.True(body!.Id > 0);
            var reloaded = await _repo.GetCustomerById(body.Id);
            Assert.NotNull(reloaded);
            Assert.Equal(dto.Email, reloaded!.Email);
            Assert.Equal(dto.First_Name, reloaded.First_Name);
        }

        [Fact]
        public async Task AddCustomer_Returns400_WhenBodyIsNull()
        {
            // Act
            var response = await _testClient.PostAsync("Customer/AddCustomer", Json(null));

            // Assert
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task AddCustomer_Returns400_WhenRequiredFieldsInvalid()
        {
            // Arrange
            var invalid = NewCustomerDto("invalid");
            invalid.Email = "not-an-email";
            invalid.First_Name = null!;

            // Act
            var response = await _testClient.PostAsync("Customer/AddCustomer", Json(invalid));

            // Assert
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task UpdateCustomer_Returns200_AndPersistsChanges()
        {
            // Arrange
            var created = await CreateCustomerAsync();
            var update = new CustomerUpdateDto
            {
                First_Name = "Updated",
                Last_Name = _marker,
                Phone = "5550199",
                Email = $"{_marker}-updated@example.com",
                Address = "99 Updated Ave",
                City = "Holland",
                State = "OH",
                Zip = "43001",
                Status = "inactive"
            };

            // Act
            var response = await _testClient.PutAsync($"Customer/UpdateCustomer?id={created.Id}", Json(update));

            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var reloaded = await _repo.GetCustomerById(created.Id);
            Assert.NotNull(reloaded);
            Assert.Equal("Updated", reloaded!.First_Name);
            Assert.Equal(update.Email, reloaded.Email);
            Assert.Equal("Holland", reloaded.City);
            Assert.Equal("inactive", reloaded.Status);
        }

        [Fact]
        public async Task UpdateCustomer_Returns404_WhenNotFound()
        {
            // Arrange
            var update = new CustomerUpdateDto
            {
                First_Name = "Ghost",
                Last_Name = _marker,
                Email = $"{_marker}-ghost@example.com",
                Status = "active"
            };

            // Act
            var response = await _testClient.PutAsync("Customer/UpdateCustomer?id=-1", Json(update));

            // Assert
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        [Fact]
        public async Task UpdateCustomer_Returns400_WhenBodyIsNull()
        {
            // Arrange
            var created = await CreateCustomerAsync();

            // Act
            var response = await _testClient.PutAsync($"Customer/UpdateCustomer?id={created.Id}", Json(null));

            // Assert
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task DeleteCustomer_Returns204_AndRemovesCustomer()
        {
            // Arrange
            var created = await CreateCustomerAsync();

            // Act
            var response = await _testClient.DeleteAsync($"Customer/DeleteCustomer?id={created.Id}");

            // Assert
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            Assert.Null(await _repo.GetCustomerById(created.Id));
        }

        [Fact]
        public async Task DeleteCustomer_Returns404_WhenNotFound()
        {
            // Act
            var response = await _testClient.DeleteAsync("Customer/DeleteCustomer?id=-1");

            // Assert
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }
}
