using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.Repositories;
using NPMotors.Repository.RequestFeatures;

namespace NPMotors.Repository.Tests.DataAccess
{
    public class EmployeeRepositoryTest : TestBase, IAsyncLifetime
    {
        private readonly EmployeeRepository _repository;
        private readonly string _marker = $"itest-{Guid.NewGuid():N}".Substring(0, 18);
        private readonly List<long> _insertedIds = new();
        private EmployeeResponseDto? _insertedEmployee;

        public EmployeeRepositoryTest()
        {
            _repository = new EmployeeRepository(_repositoryContext);
        }

        public Task InitializeAsync() => Task.CompletedTask;

        public async Task DisposeAsync()
        {
            foreach (var id in _insertedIds)
            {
                await _repository.DeleteEmployee(id);
            }
        }

        // Reuses an existing test row (matched by this test run's marker) if present, otherwise inserts one.
        private async Task<EmployeeResponseDto> InsertTestEmployeeAsync(
            string suffix = "a", string status = "active", string? department = null, decimal? commission = null)
        {
            var firstName = $"Test{suffix}";

            var existing = await _repository.GetAllEmployees(new EmployeeParameters { SearchTerm = _marker });
            var match = existing.FirstOrDefault(e => e.First_Name == firstName);
            if (match != null)
            {
                _insertedEmployee = match;
                return match;
            }

            var created = await _repository.AddEmployee(new EmployeeCreateDto
            {
                First_Name = firstName,
                Last_Name = _marker,
                Department = department ?? $"{_marker}-dept",
                Job = "Tester",
                Salary = 50000m,
                Bonus = 1000m,
                Commission_Rate = commission,
                Status = status
            });

            _insertedIds.Add(created.Id);
            _insertedEmployee = created;
            return created;
        }

        [Fact]
        public async Task GetAllEmployees_ReturnsInsertedEmployee_WhenSearchTermMatches()
        {
            // Arrange
            var inserted = await InsertTestEmployeeAsync();

            // Act
            var result = await _repository.GetAllEmployees(new EmployeeParameters { SearchTerm = _marker });

            // Assert
            Assert.NotNull(result);
            var found = result.FirstOrDefault(e => e.Id == inserted.Id);
            Assert.NotNull(found);
            Assert.Equal(_insertedEmployee!.First_Name, found!.First_Name);
            Assert.Equal("Tester", found.Job);
            Assert.Equal(1, result.MetaData.TotalCount);
        }

        [Fact]
        public async Task GetAllEmployees_SearchIsCaseInsensitive()
        {
            // Arrange
            var inserted = await InsertTestEmployeeAsync();

            // Act
            var result = await _repository.GetAllEmployees(new EmployeeParameters { SearchTerm = _marker.ToUpperInvariant() });

            // Assert
            Assert.Contains(result, e => e.Id == inserted.Id);
        }

        [Fact]
        public async Task GetAllEmployees_FiltersByStatus()
        {
            // Arrange
            var active = await InsertTestEmployeeAsync("active", "active");
            var inactive = await InsertTestEmployeeAsync("inactive", "inactive");

            // Act
            var inactiveResult = await _repository.GetAllEmployees(
                new EmployeeParameters { SearchTerm = _marker, Status = "inactive" });
            var activeResult = await _repository.GetAllEmployees(
                new EmployeeParameters { SearchTerm = _marker, Status = "ACTIVE" });

            // Assert
            Assert.Single(inactiveResult);
            Assert.Equal(inactive.Id, inactiveResult[0].Id);
            Assert.Single(activeResult);
            Assert.Equal(active.Id, activeResult[0].Id);
        }

        [Fact]
        public async Task GetAllEmployees_FiltersByDepartment_CaseInsensitive()
        {
            // Arrange
            var sales = await InsertTestEmployeeAsync("sales", department: $"{_marker}-sales");
            await InsertTestEmployeeAsync("service", department: $"{_marker}-service");

            // Act
            var result = await _repository.GetAllEmployees(
                new EmployeeParameters { SearchTerm = _marker, Department = $"{_marker}-SALES" });

            // Assert
            Assert.Single(result);
            Assert.Equal(sales.Id, result[0].Id);
        }

        [Fact]
        public async Task GetAllEmployees_FiltersBySalesperson()
        {
            // Arrange
            var salesperson = await InsertTestEmployeeAsync("seller", commission: 5m);
            var other = await InsertTestEmployeeAsync("mechanic", commission: 0m);

            // Act
            var salespeople = await _repository.GetAllEmployees(
                new EmployeeParameters { SearchTerm = _marker, IsSalesperson = true });
            var nonSalespeople = await _repository.GetAllEmployees(
                new EmployeeParameters { SearchTerm = _marker, IsSalesperson = false });

            // Assert
            Assert.Single(salespeople);
            Assert.Equal(salesperson.Id, salespeople[0].Id);
            Assert.True(salespeople[0].Is_Salesperson);
            Assert.Single(nonSalespeople);
            Assert.Equal(other.Id, nonSalespeople[0].Id);
            Assert.False(nonSalespeople[0].Is_Salesperson);
        }

        [Fact]
        public async Task GetAllEmployees_ReturnsRequestedPageAndMetaData()
        {
            // Arrange
            await InsertTestEmployeeAsync("one");
            await InsertTestEmployeeAsync("two");

            // Act
            var page1 = await _repository.GetAllEmployees(
                new EmployeeParameters { SearchTerm = _marker, PageNumber = 1, PageSize = 1 });
            var page2 = await _repository.GetAllEmployees(
                new EmployeeParameters { SearchTerm = _marker, PageNumber = 2, PageSize = 1 });

            // Assert
            Assert.Single(page1);
            Assert.Single(page2);
            Assert.NotEqual(page1[0].Id, page2[0].Id);
            Assert.Equal(2, page1.MetaData.TotalCount);
            Assert.Equal(2, page1.MetaData.TotalPages);
            Assert.Equal(2, page2.MetaData.CurrentPage);
        }

        [Fact]
        public async Task GetAllEmployees_ReturnsEmpty_WhenNothingMatches()
        {
            // Arrange
            var parameters = new EmployeeParameters { SearchTerm = $"no-such-employee-{Guid.NewGuid():N}" };

            // Act
            var result = await _repository.GetAllEmployees(parameters);

            // Assert
            Assert.Empty(result);
            Assert.Equal(0, result.MetaData.TotalCount);
        }

        [Fact]
        public async Task GetActiveSalespeople_ReturnsOnlyActiveEmployeesWithCommission()
        {
            // Arrange
            var activeSeller = await InsertTestEmployeeAsync("activeseller", "active", commission: 5m);
            var inactiveSeller = await InsertTestEmployeeAsync("inactiveseller", "inactive", commission: 5m);
            var activeNonSeller = await InsertTestEmployeeAsync("nonseller", "active", commission: 0m);

            // Act
            var result = (await _repository.GetActiveSalespeople())
                .Where(e => e.Last_Name == _marker)
                .ToList();

            // Assert
            Assert.Contains(result, e => e.Id == activeSeller.Id);
            Assert.DoesNotContain(result, e => e.Id == inactiveSeller.Id);
            Assert.DoesNotContain(result, e => e.Id == activeNonSeller.Id);
        }

        [Fact]
        public async Task AddEmployee_InsertsEmployeeAndReturnsIt()
        {
            // Arrange
            var dto = new EmployeeCreateDto
            {
                First_Name = "Created",
                Last_Name = _marker,
                Department = "Sales",
                Job = "Account Executive",
                Salary = 62000.50m,
                Bonus = 2500m,
                Commission_Rate = 7.5m,
                Status = "INACTIVE"
            };

            // Act
            var created = await _repository.AddEmployee(dto);
            _insertedIds.Add(created.Id);
            var reloaded = await _repository.GetEmployeeById(created.Id);

            // Assert
            Assert.True(created.Id > 0);
            Assert.NotNull(reloaded);
            Assert.Equal(dto.First_Name, reloaded!.First_Name);
            Assert.Equal(dto.Department, reloaded.Department);
            Assert.Equal(dto.Job, reloaded.Job);
            Assert.Equal(62000.50m, reloaded.Salary);
            Assert.Equal(2500m, reloaded.Bonus);
            Assert.Equal(7.5m, reloaded.Commission_Rate);
            Assert.True(reloaded.Is_Salesperson);
            Assert.Equal("inactive", reloaded.Status);
        }

        [Fact]
        public async Task GetEmployeeById_ReturnsEmployee_WhenExists()
        {
            // Arrange
            var inserted = await InsertTestEmployeeAsync();

            // Act
            var result = await _repository.GetEmployeeById(inserted.Id);

            // Assert
            Assert.NotNull(result);
            Assert.Equal(inserted.Id, result!.Id);
            Assert.Equal(inserted.First_Name, result.First_Name);
            Assert.Equal(inserted.Last_Name, result.Last_Name);
        }

        [Fact]
        public async Task GetEmployeeById_ReturnsNull_WhenNotFound()
        {
            // Act
            var result = await _repository.GetEmployeeById(-1);

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public async Task UpdateEmployee_UpdatesEditableFields()
        {
            // Arrange
            var inserted = await InsertTestEmployeeAsync();
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
            var updated = await _repository.UpdateEmployee(inserted.Id, update);
            var reloaded = await _repository.GetEmployeeById(inserted.Id);

            // Assert
            Assert.NotNull(updated);
            Assert.NotNull(reloaded);
            Assert.Equal(inserted.Id, reloaded!.Id);
            Assert.Equal("Updated", reloaded.First_Name);
            Assert.Equal("Service", reloaded.Department);
            Assert.Equal("Manager", reloaded.Job);
            Assert.Equal(80000m, reloaded.Salary);
            Assert.Equal(5000m, reloaded.Bonus);
            Assert.Equal(10m, reloaded.Commission_Rate);
            Assert.Equal("inactive", reloaded.Status);
        }

        [Fact]
        public async Task UpdateEmployee_ReturnsNull_WhenNotFound()
        {
            // Arrange
            var update = new EmployeeUpdateDto
            {
                First_Name = "Ghost",
                Last_Name = _marker,
                Status = "active"
            };

            // Act
            var result = await _repository.UpdateEmployee(-1, update);

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public async Task DeleteEmployee_RemovesEmployee()
        {
            // Arrange
            var inserted = await InsertTestEmployeeAsync();

            // Act
            var deleted = await _repository.DeleteEmployee(inserted.Id);
            var reloaded = await _repository.GetEmployeeById(inserted.Id);

            // Assert
            Assert.True(deleted);
            Assert.Null(reloaded);
        }

        [Fact]
        public async Task DeleteEmployee_ReturnsFalse_WhenNotFound()
        {
            // Act
            var deleted = await _repository.DeleteEmployee(-1);

            // Assert
            Assert.False(deleted);
        }

        [Fact]
        public async Task IsEmployeeReferenced_ReturnsFalse_WhenNoInvoiceReferencesEmployee()
        {
            // Arrange
            var inserted = await InsertTestEmployeeAsync();

            // Act
            var referenced = await _repository.IsEmployeeReferenced(inserted.Id);

            // Assert
            Assert.False(referenced);
        }
    }
}
