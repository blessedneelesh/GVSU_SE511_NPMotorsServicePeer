using Dapper;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.Repositories;
using NPMotors.Repository.RequestFeatures;

namespace NPMotors.Repository.Tests.DataAccess
{
    public class VendorRepositoryTest : TestBase, IAsyncLifetime
    {
        private readonly VendorRepository _repository;
        private readonly string _marker = $"itest-{Guid.NewGuid():N}".Substring(0, 18);
        private readonly List<long> _insertedIds = new();
        private VendorResponseDto? _insertedVendor;

        public VendorRepositoryTest()
        {
            _repository = new VendorRepository(_repositoryContext);
        }

        public Task InitializeAsync() => Task.CompletedTask;

        public async Task DisposeAsync()
        {
            foreach (var id in _insertedIds)
            {
                await _repository.DeleteVendor(id);
            }
        }

        // Reuses an existing test row (matched by this test run's marker) if present, otherwise inserts one.
        private async Task<VendorResponseDto> InsertTestVendorAsync(
            string suffix = "a", string status = "active", string type = "Parts")
        {
            var email = $"{_marker}-{suffix}@example.com";

            var existing = await _repository.GetAllVendors(new VendorParameters { SearchTerm = email });
            var match = existing.FirstOrDefault(v => v.Email == email);
            if (match != null)
            {
                _insertedVendor = match;
                return match;
            }

            var created = await _repository.AddVendor(new VendorCreateDto
            {
                Name = $"{_marker} Supply {suffix}",
                Contact = "Test Contact",
                Phone = "5550100",
                Email = email,
                Address = "1 Test St",
                Type = type,
                Status = status
            });

            _insertedIds.Add(created.Id);
            _insertedVendor = created;
            return created;
        }

        [Fact]
        public async Task GetAllVendors_ReturnsInsertedVendor_WhenSearchTermMatches()
        {
            // Arrange
            var inserted = await InsertTestVendorAsync();

            // Act
            var result = await _repository.GetAllVendors(new VendorParameters { SearchTerm = _marker });

            // Assert
            Assert.NotNull(result);
            var found = result.FirstOrDefault(v => v.Id == inserted.Id);
            Assert.NotNull(found);
            Assert.Equal(_insertedVendor!.Email, found!.Email);
            Assert.Equal("Parts", found.Type);
            Assert.Equal(1, result.MetaData.TotalCount);
        }

        [Fact]
        public async Task GetAllVendors_SearchIsCaseInsensitive()
        {
            // Arrange
            var inserted = await InsertTestVendorAsync();

            // Act
            var result = await _repository.GetAllVendors(new VendorParameters { SearchTerm = _marker.ToUpperInvariant() });

            // Assert
            Assert.Contains(result, v => v.Id == inserted.Id);
        }

        [Fact]
        public async Task GetAllVendors_FiltersByStatus()
        {
            // Arrange
            var active = await InsertTestVendorAsync("active", "active");
            var inactive = await InsertTestVendorAsync("inactive", "inactive");

            // Act
            var inactiveResult = await _repository.GetAllVendors(
                new VendorParameters { SearchTerm = _marker, Status = "inactive" });
            var activeResult = await _repository.GetAllVendors(
                new VendorParameters { SearchTerm = _marker, Status = "ACTIVE" });

            // Assert
            Assert.Single(inactiveResult);
            Assert.Equal(inactive.Id, inactiveResult[0].Id);
            Assert.Single(activeResult);
            Assert.Equal(active.Id, activeResult[0].Id);
        }

        [Fact]
        public async Task GetAllVendors_FiltersByType()
        {
            // Arrange
            var parts = await InsertTestVendorAsync("parts", type: "Parts");
            await InsertTestVendorAsync("vehicle", type: "Vehicle");

            // Act
            var result = await _repository.GetAllVendors(
                new VendorParameters { SearchTerm = _marker, Type = "PARTS" });

            // Assert
            Assert.Single(result);
            Assert.Equal(parts.Id, result[0].Id);
        }

        [Fact]
        public async Task GetAllVendors_ReturnsRequestedPageAndMetaData()
        {
            // Arrange
            await InsertTestVendorAsync("one");
            await InsertTestVendorAsync("two");

            // Act
            var page1 = await _repository.GetAllVendors(
                new VendorParameters { SearchTerm = _marker, PageNumber = 1, PageSize = 1 });
            var page2 = await _repository.GetAllVendors(
                new VendorParameters { SearchTerm = _marker, PageNumber = 2, PageSize = 1 });

            // Assert
            Assert.Single(page1);
            Assert.Single(page2);
            Assert.NotEqual(page1[0].Id, page2[0].Id);
            Assert.Equal(2, page1.MetaData.TotalCount);
            Assert.Equal(2, page1.MetaData.TotalPages);
            Assert.Equal(2, page2.MetaData.CurrentPage);
        }

        [Fact]
        public async Task GetAllVendors_ReturnsEmpty_WhenNothingMatches()
        {
            // Arrange
            var parameters = new VendorParameters { SearchTerm = $"no-such-vendor-{Guid.NewGuid():N}" };

            // Act
            var result = await _repository.GetAllVendors(parameters);

            // Assert
            Assert.Empty(result);
            Assert.Equal(0, result.MetaData.TotalCount);
        }

        [Fact]
        public async Task GetActiveVendors_ExcludesInactiveAndFiltersByType()
        {
            // Arrange
            var activeParts = await InsertTestVendorAsync("activeparts", "active", "Parts");
            var inactiveParts = await InsertTestVendorAsync("inactiveparts", "inactive", "Parts");
            var activeVehicle = await InsertTestVendorAsync("activevehicle", "active", "Vehicle");

            // Act
            var all = (await _repository.GetActiveVendors())
                .Where(v => v.Name.StartsWith(_marker)).ToList();
            var partsOnly = (await _repository.GetActiveVendors("parts"))
                .Where(v => v.Name.StartsWith(_marker)).ToList();

            // Assert
            Assert.Contains(all, v => v.Id == activeParts.Id);
            Assert.Contains(all, v => v.Id == activeVehicle.Id);
            Assert.DoesNotContain(all, v => v.Id == inactiveParts.Id);
            Assert.Single(partsOnly);
            Assert.Equal(activeParts.Id, partsOnly[0].Id);
        }

        [Fact]
        public async Task AddVendor_InsertsVendorWithZeroBalanceAndTotalPurchases()
        {
            // Arrange
            var dto = new VendorCreateDto
            {
                Name = $"{_marker} Created",
                Contact = "Jane Doe",
                Phone = "5550101",
                Email = $"{_marker}-create@example.com",
                Address = "2 Test St",
                Type = "Vehicle",
                Status = "INACTIVE"
            };

            // Act
            var created = await _repository.AddVendor(dto);
            _insertedIds.Add(created.Id);
            var reloaded = await _repository.GetVendorById(created.Id);

            // Assert
            Assert.True(created.Id > 0);
            Assert.NotNull(reloaded);
            Assert.Equal(dto.Name, reloaded!.Name);
            Assert.Equal(dto.Email, reloaded.Email);
            Assert.Equal("Vehicle", reloaded.Type);
            Assert.Equal("inactive", reloaded.Status);
            Assert.Equal(0m, reloaded.Balance);
            Assert.Equal(0m, reloaded.TotalPurchases);
        }

        [Fact]
        public async Task GetVendorById_ReturnsVendor_WhenExists()
        {
            // Arrange
            var inserted = await InsertTestVendorAsync();

            // Act
            var result = await _repository.GetVendorById(inserted.Id);

            // Assert
            Assert.NotNull(result);
            Assert.Equal(inserted.Id, result!.Id);
            Assert.Equal(inserted.Email, result.Email);
            Assert.Equal(inserted.Name, result.Name);
        }

        [Fact]
        public async Task GetVendorById_ReturnsNull_WhenNotFound()
        {
            // Act
            var result = await _repository.GetVendorById(-1);

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public async Task UpdateVendor_UpdatesEditableFields_AndLeavesBalanceAndTotalPurchasesUnchanged()
        {
            // Arrange
            var inserted = await InsertTestVendorAsync();
            using (var connection = _repositoryContext.GetDatabaseConnection())
            {
                await connection.ExecuteAsync(
                    "UPDATE vendor SET balance = 123.45, total_purchases = 678.90 WHERE id = @Id",
                    new { Id = inserted.Id });
            }
            var update = new VendorUpdateDto
            {
                Name = $"{_marker} Updated",
                Contact = "New Contact",
                Phone = "5550199",
                Email = $"{_marker}-updated@example.com",
                Address = "99 Updated Ave",
                Type = "Service",
                Status = "active"
            };

            // Act
            var updated = await _repository.UpdateVendor(inserted.Id, update);
            var reloaded = await _repository.GetVendorById(inserted.Id);

            // Assert
            Assert.NotNull(updated);
            Assert.NotNull(reloaded);
            Assert.Equal(inserted.Id, reloaded!.Id);
            Assert.Equal(update.Name, reloaded.Name);
            Assert.Equal(update.Email, reloaded.Email);
            Assert.Equal("Service", reloaded.Type);
            Assert.Equal(123.45m, reloaded.Balance);
            Assert.Equal(678.90m, reloaded.TotalPurchases);
        }

        [Fact]
        public async Task UpdateVendor_CanSetStatusToInactive()
        {
            // Arrange
            var inserted = await InsertTestVendorAsync();
            var update = new VendorUpdateDto
            {
                Name = inserted.Name,
                Type = inserted.Type!,
                Status = "Inactive"
            };

            // Act
            var updated = await _repository.UpdateVendor(inserted.Id, update);
            var activeVendors = await _repository.GetActiveVendors();

            // Assert
            Assert.NotNull(updated);
            Assert.Equal("inactive", updated!.Status);
            Assert.DoesNotContain(activeVendors, v => v.Id == inserted.Id);
        }

        [Fact]
        public async Task UpdateVendor_ReturnsNull_WhenNotFound()
        {
            // Arrange
            var update = new VendorUpdateDto
            {
                Name = $"{_marker} Ghost",
                Type = "Parts",
                Status = "active"
            };

            // Act
            var result = await _repository.UpdateVendor(-1, update);

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public async Task DeleteVendor_RemovesVendor()
        {
            // Arrange
            var inserted = await InsertTestVendorAsync();

            // Act
            var deleted = await _repository.DeleteVendor(inserted.Id);
            var reloaded = await _repository.GetVendorById(inserted.Id);

            // Assert
            Assert.True(deleted);
            Assert.Null(reloaded);
        }

        [Fact]
        public async Task DeleteVendor_ReturnsFalse_WhenNotFound()
        {
            // Act
            var deleted = await _repository.DeleteVendor(-1);

            // Assert
            Assert.False(deleted);
        }

        [Fact]
        public async Task IsVendorReferenced_ReturnsFalse_WhenNothingReferencesVendor()
        {
            // Arrange
            var inserted = await InsertTestVendorAsync();

            // Act
            var referenced = await _repository.IsVendorReferenced(inserted.Id);

            // Assert
            Assert.False(referenced);
        }
    }
}
