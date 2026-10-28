using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.Repositories;
using NPMotors.Repository.RequestFeatures;

namespace NPMotors.Repository.Tests.DataAccess
{
    public class CustomerRepositoryTest : TestBase, IAsyncLifetime
    {
        private readonly CustomerRepository _repository;
        private readonly string _marker = $"itest-{Guid.NewGuid():N}".Substring(0, 18);
        private readonly List<long> _insertedIds = new();
        private CustomerResponseDto? _insertedCustomer;

        public CustomerRepositoryTest()
        {
            _repository = new CustomerRepository(_repositoryContext);
        }

        public Task InitializeAsync() => Task.CompletedTask;

        public async Task DisposeAsync()
        {
            foreach (var id in _insertedIds)
            {
                await _repository.DeleteCustomer(id);
            }
        }

        // Reuses an existing test row (matched by this test run's marker) if present, otherwise inserts one.
        private async Task<CustomerResponseDto> InsertTestCustomerAsync(string suffix = "a", string status = "active")
        {
            var email = $"{_marker}-{suffix}@example.com";

            var existing = await _repository.GetAllCustomers(new CustomerParameters { SearchTerm = email });
            var match = existing.FirstOrDefault(c => c.Email == email);
            if (match != null)
            {
                _insertedCustomer = match;
                return match;
            }

            var created = await _repository.AddCustomer(new CustomerCreateDto
            {
                First_Name = $"Test{suffix}",
                Last_Name = _marker,
                Phone = "5550100",
                Email = email,
                Address = "1 Test St",
                City = "Allendale",
                State = "MI",
                Zip = "49401",
                Status = status
            });

            _insertedIds.Add(created.Id);
            _insertedCustomer = created;
            return created;
        }

        [Fact]
        public async Task GetAllCustomers_ReturnsInsertedCustomer_WhenSearchTermMatches()
        {
            // Arrange
            var inserted = await InsertTestCustomerAsync();

            // Act
            var result = await _repository.GetAllCustomers(new CustomerParameters { SearchTerm = _marker });

            // Assert
            Assert.NotNull(result);
            var found = result.FirstOrDefault(c => c.Id == inserted.Id);
            Assert.NotNull(found);
            Assert.Equal(_insertedCustomer!.Email, found!.Email);
            Assert.Equal("MI", found.State);
            Assert.Equal(1, result.MetaData.TotalCount);
        }

        [Fact]
        public async Task GetAllCustomers_SearchIsCaseInsensitive()
        {
            // Arrange
            var inserted = await InsertTestCustomerAsync();

            // Act
            var result = await _repository.GetAllCustomers(new CustomerParameters { SearchTerm = _marker.ToUpperInvariant() });

            // Assert
            Assert.Contains(result, c => c.Id == inserted.Id);
        }

        [Fact]
        public async Task GetAllCustomers_FiltersByStatus()
        {
            // Arrange
            var active = await InsertTestCustomerAsync("active", "active");
            var inactive = await InsertTestCustomerAsync("inactive", "inactive");

            // Act
            var inactiveResult = await _repository.GetAllCustomers(
                new CustomerParameters { SearchTerm = _marker, Status = "inactive" });
            var activeResult = await _repository.GetAllCustomers(
                new CustomerParameters { SearchTerm = _marker, Status = "ACTIVE" });

            // Assert
            Assert.Single(inactiveResult);
            Assert.Equal(inactive.Id, inactiveResult[0].Id);
            Assert.Single(activeResult);
            Assert.Equal(active.Id, activeResult[0].Id);
        }

        [Fact]
        public async Task GetAllCustomers_ReturnsRequestedPageAndMetaData()
        {
            // Arrange
            await InsertTestCustomerAsync("one");
            await InsertTestCustomerAsync("two");

            // Act
            var page1 = await _repository.GetAllCustomers(
                new CustomerParameters { SearchTerm = _marker, PageNumber = 1, PageSize = 1 });
            var page2 = await _repository.GetAllCustomers(
                new CustomerParameters { SearchTerm = _marker, PageNumber = 2, PageSize = 1 });

            // Assert
            Assert.Single(page1);
            Assert.Single(page2);
            Assert.NotEqual(page1[0].Id, page2[0].Id);
            Assert.Equal(2, page1.MetaData.TotalCount);
            Assert.Equal(2, page1.MetaData.TotalPages);
            Assert.Equal(2, page2.MetaData.CurrentPage);
        }

        [Fact]
        public async Task GetAllCustomers_ReturnsEmpty_WhenNothingMatches()
        {
            // Arrange
            var parameters = new CustomerParameters { SearchTerm = $"no-such-customer-{Guid.NewGuid():N}" };

            // Act
            var result = await _repository.GetAllCustomers(parameters);

            // Assert
            Assert.Empty(result);
            Assert.Equal(0, result.MetaData.TotalCount);
        }

        [Fact]
        public async Task AddCustomer_InsertsCustomerAndReturnsIt()
        {
            // Arrange
            var dto = new CustomerCreateDto
            {
                First_Name = "Created",
                Last_Name = _marker,
                Phone = "5550101",
                Email = $"{_marker}-create@example.com",
                Address = "2 Test St",
                City = "Grand Rapids",
                State = "MI",
                Zip = "49503",
                Status = "INACTIVE"
            };

            // Act
            var created = await _repository.AddCustomer(dto);
            _insertedIds.Add(created.Id);
            var reloaded = await _repository.GetCustomerById(created.Id);

            // Assert
            Assert.True(created.Id > 0);
            Assert.NotNull(reloaded);
            Assert.Equal(dto.First_Name, reloaded!.First_Name);
            Assert.Equal(dto.Email, reloaded.Email);
            Assert.Equal(dto.City, reloaded.City);
            Assert.Equal("inactive", reloaded.Status);
            Assert.Equal(0m, reloaded.Balance);
            Assert.Equal(0m, reloaded.TotalPurchases);
        }

        [Fact]
        public async Task GetCustomerById_ReturnsCustomer_WhenExists()
        {
            // Arrange
            var inserted = await InsertTestCustomerAsync();

            // Act
            var result = await _repository.GetCustomerById(inserted.Id);

            // Assert
            Assert.NotNull(result);
            Assert.Equal(inserted.Id, result!.Id);
            Assert.Equal(inserted.Email, result.Email);
            Assert.Equal(inserted.Last_Name, result.Last_Name);
        }

        [Fact]
        public async Task GetCustomerById_ReturnsNull_WhenNotFound()
        {
            // Act
            var result = await _repository.GetCustomerById(-1);

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public async Task UpdateCustomer_UpdatesEditableFields()
        {
            // Arrange
            var inserted = await InsertTestCustomerAsync();
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
            var updated = await _repository.UpdateCustomer(inserted.Id, update);
            var reloaded = await _repository.GetCustomerById(inserted.Id);

            // Assert
            Assert.NotNull(updated);
            Assert.NotNull(reloaded);
            Assert.Equal(inserted.Id, reloaded!.Id);
            Assert.Equal("Updated", reloaded.First_Name);
            Assert.Equal(update.Email, reloaded.Email);
            Assert.Equal("Holland", reloaded.City);
            Assert.Equal("OH", reloaded.State);
            Assert.Equal("inactive", reloaded.Status);
            Assert.Equal(inserted.Balance, reloaded.Balance);
        }

        [Fact]
        public async Task UpdateCustomer_ReturnsNull_WhenNotFound()
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
            var result = await _repository.UpdateCustomer(-1, update);

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public async Task DeleteCustomer_RemovesCustomer()
        {
            // Arrange
            var inserted = await InsertTestCustomerAsync();

            // Act
            var deleted = await _repository.DeleteCustomer(inserted.Id);
            var reloaded = await _repository.GetCustomerById(inserted.Id);

            // Assert
            Assert.True(deleted);
            Assert.Null(reloaded);
        }

        [Fact]
        public async Task DeleteCustomer_ReturnsFalse_WhenNotFound()
        {
            // Act
            var deleted = await _repository.DeleteCustomer(-1);

            // Assert
            Assert.False(deleted);
        }
    }
}
