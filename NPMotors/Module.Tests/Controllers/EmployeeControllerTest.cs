using System.Net;
using System.Text;
using Newtonsoft.Json;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.Repositories;

namespace Module.Tests.Controllers
{
    public class EmployeeControllerTest : TestBase, IAsyncLifetime
    {
        private EmployeeRepository _repo => new(_repositoryContext);
        private readonly string _marker = $"itest-{Guid.NewGuid():N}".Substring(0, 18);
        private readonly List<long> _createdIds = new();

        private class GetAllResponse
        {
            public List<EmployeeResponseDto> Items { get; set; } = new();
        }

        public Task InitializeAsync() => Task.CompletedTask;

        public async Task DisposeAsync()
        {
            foreach (var id in _createdIds)
            {
                try
                {
                    await _repo.DeleteEmployee(id);
                }
                catch
                {
                    // Keep going so one failed delete does not block the rest.
                }
            }
        }

        private EmployeeCreateDto NewEmployeeDto(string suffix) => new()
        {
            First_Name = $"Test{suffix}",
            Last_Name = _marker,
            Department = $"{_marker}-dept",
            Job = "Tester",
            Salary = 50000m,
            Bonus = 1000m,
            Commission_Rate = 5m,
            Status = "active"
        };

        private async Task<EmployeeResponseDto> CreateEmployeeAsync(string suffix = "a", Action<EmployeeCreateDto>? customize = null)
        {
            var dto = NewEmployeeDto(suffix);
            customize?.Invoke(dto);
            var created = await _repo.AddEmployee(dto);
            _createdIds.Add(created.Id);
            return created;
        }

        private static StringContent Json(object? body) =>
            new(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");

        private static async Task<T?> ReadAsync<T>(HttpResponseMessage response) =>
            JsonConvert.DeserializeObject<T>(await response.Content.ReadAsStringAsync());

        [Fact]
        public async Task GetAllEmployees_Returns200_AndContainsInsertedEmployee()
        {
            // Arrange
            var created = await CreateEmployeeAsync();

            // Act
            var response = await _testClient.GetAsync($"Employee/GetAllEmployees?SearchTerm={_marker}");
            var body = await ReadAsync<GetAllResponse>(response);

            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.NotNull(body);
            Assert.Contains(body!.Items, e => e.Id == created.Id);
        }

        [Fact]
        public async Task GetAllEmployees_FiltersByStatusAndDepartment()
        {
            // Arrange
            var active = await CreateEmployeeAsync("active", d => d.Department = $"{_marker}-sales");
            var inactive = await CreateEmployeeAsync("inactive", d =>
            {
                d.Department = $"{_marker}-service";
                d.Status = "inactive";
            });

            // Act
            var byStatus = await ReadAsync<GetAllResponse>(
                await _testClient.GetAsync($"Employee/GetAllEmployees?SearchTerm={_marker}&Status=inactive"));
            var byDepartment = await ReadAsync<GetAllResponse>(
                await _testClient.GetAsync($"Employee/GetAllEmployees?SearchTerm={_marker}&Department={_marker}-sales"));

            // Assert
            Assert.Single(byStatus!.Items);
            Assert.Equal(inactive.Id, byStatus.Items[0].Id);
            Assert.Single(byDepartment!.Items);
            Assert.Equal(active.Id, byDepartment.Items[0].Id);
        }

        [Fact]
        public async Task GetAllEmployees_ReturnsPagingMetaData()
        {
            // Arrange
            await CreateEmployeeAsync("one");
            await CreateEmployeeAsync("two");

            // Act
            var response = await _testClient.GetAsync($"Employee/GetAllEmployees?SearchTerm={_marker}&PageNumber=2&PageSize=1");
            var body = await ReadAsync<GetAllResponse>(response);
            var json = await response.Content.ReadAsStringAsync();

            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Single(body!.Items);
            Assert.Contains("\"metaData\"", json);
            Assert.Contains("\"totalCount\":2", json);
        }

        [Fact]
        public async Task GetEmployeeById_Returns200_WithMatchingFields()
        {
            // Arrange
            var created = await CreateEmployeeAsync();

            // Act
            var response = await _testClient.GetAsync($"Employee/GetEmployeeById?id={created.Id}");
            var body = await ReadAsync<EmployeeResponseDto>(response);

            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.NotNull(body);
            Assert.Equal(created.Id, body!.Id);
            Assert.Equal(created.First_Name, body.First_Name);
            Assert.Equal(created.Department, body.Department);
        }

        [Fact]
        public async Task GetEmployeeById_Returns404_WhenNotFound()
        {
            // Act
            var response = await _testClient.GetAsync("Employee/GetEmployeeById?id=-1");

            // Assert
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        [Fact]
        public async Task GetActiveSalespeople_Returns200_WithOnlyActiveSalespeople()
        {
            // Arrange
            var seller = await CreateEmployeeAsync("seller");
            var inactiveSeller = await CreateEmployeeAsync("inactiveseller", d => d.Status = "inactive");
            var nonSeller = await CreateEmployeeAsync("nonseller", d => d.Commission_Rate = 0m);

            // Act
            var response = await _testClient.GetAsync("Employee/GetActiveSalespeople");
            var body = await ReadAsync<List<EmployeeResponseDto>>(response);

            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.NotNull(body);
            Assert.Contains(body!, e => e.Id == seller.Id);
            Assert.DoesNotContain(body!, e => e.Id == inactiveSeller.Id);
            Assert.DoesNotContain(body!, e => e.Id == nonSeller.Id);
        }

        [Fact]
        public async Task AddEmployee_Returns201_AndPersistsEmployee()
        {
            // Arrange
            var dto = NewEmployeeDto("post");

            // Act
            var response = await _testClient.PostAsync("Employee/AddEmployee", Json(dto));
            var body = await ReadAsync<EmployeeResponseDto>(response);
            if (body != null && body.Id > 0)
                _createdIds.Add(body.Id);

            // Assert
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            Assert.NotNull(body);
            Assert.True(body!.Id > 0);
            var reloaded = await _repo.GetEmployeeById(body.Id);
            Assert.NotNull(reloaded);
            Assert.Equal(dto.First_Name, reloaded!.First_Name);
            Assert.Equal(dto.Salary, reloaded.Salary);
            Assert.Equal(dto.Commission_Rate, reloaded.Commission_Rate);
            Assert.True(reloaded.Is_Salesperson);
        }

        [Fact]
        public async Task AddEmployee_Returns400_WhenBodyIsNull()
        {
            // Act
            var response = await _testClient.PostAsync("Employee/AddEmployee", Json(null));

            // Assert
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task AddEmployee_Returns400_WhenNameIsMissing()
        {
            // Arrange
            var invalid = NewEmployeeDto("invalid");
            invalid.First_Name = null!;

            // Act
            var response = await _testClient.PostAsync("Employee/AddEmployee", Json(invalid));

            // Assert
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task AddEmployee_Returns400_WhenSalaryOrBonusIsNegative()
        {
            // Arrange
            var negativeSalary = NewEmployeeDto("negsalary");
            negativeSalary.Salary = -1m;
            var negativeBonus = NewEmployeeDto("negbonus");
            negativeBonus.Bonus = -1m;

            // Act
            var salaryResponse = await _testClient.PostAsync("Employee/AddEmployee", Json(negativeSalary));
            var bonusResponse = await _testClient.PostAsync("Employee/AddEmployee", Json(negativeBonus));

            // Assert
            Assert.Equal(HttpStatusCode.BadRequest, salaryResponse.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, bonusResponse.StatusCode);
        }

        [Fact]
        public async Task AddEmployee_Returns400_WhenCommissionRateOutOfRange()
        {
            // Arrange
            var tooHigh = NewEmployeeDto("high");
            tooHigh.Commission_Rate = 100.01m;
            var negative = NewEmployeeDto("low");
            negative.Commission_Rate = -0.01m;

            // Act
            var highResponse = await _testClient.PostAsync("Employee/AddEmployee", Json(tooHigh));
            var lowResponse = await _testClient.PostAsync("Employee/AddEmployee", Json(negative));

            // Assert
            Assert.Equal(HttpStatusCode.BadRequest, highResponse.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, lowResponse.StatusCode);
        }

        [Fact]
        public async Task UpdateEmployee_Returns200_AndPersistsChanges()
        {
            // Arrange
            var created = await CreateEmployeeAsync();
            var update = new EmployeeUpdateDto
            {
                First_Name = "Updated",
                Last_Name = _marker,
                Department = "Service",
                Job = "Manager",
                Salary = 80000m,
                Bonus = 5000m,
                Commission_Rate = 10m,
                Status = "inactive"
            };

            // Act
            var response = await _testClient.PutAsync($"Employee/UpdateEmployee?id={created.Id}", Json(update));

            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var reloaded = await _repo.GetEmployeeById(created.Id);
            Assert.NotNull(reloaded);
            Assert.Equal("Updated", reloaded!.First_Name);
            Assert.Equal("Service", reloaded.Department);
            Assert.Equal(80000m, reloaded.Salary);
            Assert.Equal(10m, reloaded.Commission_Rate);
            Assert.Equal("inactive", reloaded.Status);
        }

        [Fact]
        public async Task UpdateEmployee_Returns404_WhenNotFound()
        {
            // Arrange
            var update = new EmployeeUpdateDto
            {
                First_Name = "Ghost",
                Last_Name = _marker,
                Status = "active"
            };

            // Act
            var response = await _testClient.PutAsync("Employee/UpdateEmployee?id=-1", Json(update));

            // Assert
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        [Fact]
        public async Task UpdateEmployee_Returns400_WhenBodyIsNull()
        {
            // Arrange
            var created = await CreateEmployeeAsync();

            // Act
            var response = await _testClient.PutAsync($"Employee/UpdateEmployee?id={created.Id}", Json(null));

            // Assert
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task DeleteEmployee_Returns204_AndRemovesEmployee()
        {
            // Arrange
            var created = await CreateEmployeeAsync();

            // Act
            var response = await _testClient.DeleteAsync($"Employee/DeleteEmployee?id={created.Id}");

            // Assert
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            Assert.Null(await _repo.GetEmployeeById(created.Id));
        }

        [Fact]
        public async Task DeleteEmployee_Returns404_WhenNotFound()
        {
            // Act
            var response = await _testClient.DeleteAsync("Employee/DeleteEmployee?id=-1");

            // Assert
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }
}
