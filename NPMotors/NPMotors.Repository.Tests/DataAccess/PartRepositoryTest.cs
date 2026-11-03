using System.Data;
using Dapper;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.Models.Enums;
using NPMotors.Repository.Repositories;
using NPMotors.Repository.RequestFeatures;

namespace NPMotors.Repository.Tests.DataAccess
{
    public class PartRepositoryTest : TestBase, IAsyncLifetime
    {
        private readonly PartRepository _repository;
        private readonly VendorRepository _vendorRepository;
        private readonly string _marker = $"itest-{Guid.NewGuid():N}".Substring(0, 12);
        private readonly List<long> _partIds = new();
        private readonly List<long> _vendorIds = new();

        public PartRepositoryTest()
        {
            _repository = new PartRepository(_repositoryContext);
            _vendorRepository = new VendorRepository(_repositoryContext);
        }

        public Task InitializeAsync() => Task.CompletedTask;

        public async Task DisposeAsync()
        {
            using (IDbConnection connection = _repositoryContext.GetDatabaseConnection())
            {
                foreach (var id in _partIds)
                    await connection.ExecuteAsync("DELETE FROM part WHERE id = @Id", new { Id = id });
            }
            foreach (var id in _vendorIds)
                await _vendorRepository.DeleteVendor(id);
        }

        private async Task<VendorResponseDto> CreateVendorAsync(Status status = Status.Active)
        {
            var vendor = await _vendorRepository.AddVendor(new VendorCreateDto
            {
                Name = $"{_marker} Vendor",
                Type = VendorType.Parts,
                Status = status
            });
            _vendorIds.Add(vendor.Id);
            return vendor;
        }

        private async Task<PartResponseDto> CreatePartAsync(
            long vendorId, string suffix, string? name = "Brake Pad", string? category = "Brakes",
            decimal cost = 10m, decimal sell = 15m, int quantity = 10, int reorder = 5)
        {
            var created = await _repository.AddPart(new PartCreateDto
            {
                Part_Number = $"{_marker}-{suffix}",
                Name = name,
                Category = category,
                Vendor_Id = vendorId,
                Cost_Price = cost,
                Sell_Price = sell,
                Quantity = quantity,
                Reorder_Level = reorder,
                Warehouse = "Main"
            });
            _partIds.Add(created.Id);
            return created;
        }

        // The marker is part of every test part number, so searching for it isolates this test's rows.
        private PartParameters Scoped(string? category = null, bool? lowStockOnly = null, string? search = null) => new()
        {
            SearchTerm = search ?? _marker,
            Category = category,
            LowStockOnly = lowStockOnly
        };

        // ---- AddPart ----

        [Fact]
        public async Task AddPart_InsertsAllFields_AndReturnsStoredRow()
        {
            var vendor = await CreateVendorAsync();

            var created = await CreatePartAsync(vendor.Id, "A", "Brake Pad", "Brakes", 10.25m, 15.75m, 12, 4);
            var reloaded = await _repository.GetPartById(created.Id);

            Assert.True(created.Id > 0);
            Assert.NotNull(reloaded);
            Assert.Equal($"{_marker}-A", reloaded!.Part_Number);
            Assert.Equal("Brake Pad", reloaded.Name);
            Assert.Equal("Brakes", reloaded.Category);
            Assert.Equal(vendor.Id, reloaded.Vendor_Id);
            Assert.Equal(10.25m, reloaded.Cost_Price);
            Assert.Equal(15.75m, reloaded.Sell_Price);
            Assert.Equal(12, reloaded.Quantity);
            Assert.Equal(4, reloaded.Reorder_Level);
            Assert.Equal("Main", reloaded.Warehouse);
        }

        [Fact]
        public async Task AddPart_OmittedOptionalFields_DefaultToZero()
        {
            var vendor = await CreateVendorAsync();

            var created = await _repository.AddPart(new PartCreateDto { Part_Number = $"{_marker}-A", Vendor_Id = vendor.Id });
            _partIds.Add(created.Id);

            Assert.Equal(0m, created.Cost_Price);
            Assert.Equal(0m, created.Sell_Price);
            Assert.Equal(0, created.Quantity);
            Assert.Equal(0, created.Reorder_Level);
            Assert.Null(created.Name);
            Assert.Null(created.Category);
            Assert.Null(created.Warehouse);
        }

        [Fact]
        public async Task AddPart_ReturnsCalculatedFieldsAtDefaults()
        {
            var vendor = await CreateVendorAsync();

            var created = await CreatePartAsync(vendor.Id, "A", cost: 10m, quantity: 3, reorder: 5);

            Assert.Equal(0m, created.Inventory_Value);
            Assert.False(created.Is_Low_Stock);
        }

        [Fact]
        public async Task AddPart_UnknownVendor_ThrowsAndInsertsNothing()
        {
            var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
                _repository.AddPart(new PartCreateDto { Part_Number = $"{_marker}-A", Vendor_Id = -1 }));

            Assert.Equal("Vendor does not exist.", ex.Message);
            Assert.Equal(0, (await _repository.GetAllParts(Scoped())).MetaData.TotalCount);
        }

        [Fact]
        public async Task AddPart_InactiveVendor_ThrowsAndInsertsNothing()
        {
            var vendor = await CreateVendorAsync(Status.Inactive);

            var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
                _repository.AddPart(new PartCreateDto { Part_Number = $"{_marker}-A", Vendor_Id = vendor.Id }));

            Assert.Equal("Inactive vendors cannot be selected.", ex.Message);
            Assert.Equal(0, (await _repository.GetAllParts(Scoped())).MetaData.TotalCount);
        }

        [Fact]
        public async Task AddPart_DuplicatePartNumber_IsAllowed()
        {
            var vendor = await CreateVendorAsync();
            var dto = new PartCreateDto { Part_Number = $"{_marker}-DUP", Vendor_Id = vendor.Id };

            var first = await _repository.AddPart(dto);
            var second = await _repository.AddPart(dto);
            _partIds.Add(first.Id);
            _partIds.Add(second.Id);

            Assert.NotEqual(first.Id, second.Id);
            Assert.Equal(first.Part_Number, second.Part_Number);
        }

        // ---- GetAllParts: search ----

        [Fact]
        public async Task GetAllParts_SearchTerm_MatchesPartNumberPartialAndCaseInsensitive()
        {
            var vendor = await CreateVendorAsync();
            var part = await CreatePartAsync(vendor.Id, "A", name: "Alpha");
            await CreatePartAsync(vendor.Id, "B", name: "Beta");
            var mixedCase = "ItEsT-" + _marker.Substring(6) + "-A";

            var result = await _repository.GetAllParts(Scoped(search: mixedCase));

            var only = Assert.Single(result);
            Assert.Equal(part.Id, only.Id);
        }

        [Fact]
        public async Task GetAllParts_SearchTerm_MatchesNamePartialAndCaseInsensitive()
        {
            var vendor = await CreateVendorAsync();
            var token = Guid.NewGuid().ToString("N").Substring(0, 8);
            var match = await CreatePartAsync(vendor.Id, "A", name: $"Nm{token}");
            await CreatePartAsync(vendor.Id, "B", name: "Other");

            var result = await _repository.GetAllParts(Scoped(search: token.Substring(1, 5).ToUpperInvariant()));

            var only = Assert.Single(result);
            Assert.Equal(match.Id, only.Id);
        }

        // ---- GetAllParts: category and low-stock filters ----

        [Fact]
        public async Task GetAllParts_Category_IsExactMatch()
        {
            var vendor = await CreateVendorAsync();
            var token = Guid.NewGuid().ToString("N").Substring(0, 8);
            var match = await CreatePartAsync(vendor.Id, "A", category: $"Cat{token}");
            await CreatePartAsync(vendor.Id, "B", category: $"Cat{token}X");
            await CreatePartAsync(vendor.Id, "C", category: "Other");

            var result = await _repository.GetAllParts(Scoped(category: $"Cat{token}"));

            var only = Assert.Single(result);
            Assert.Equal(match.Id, only.Id);
        }

        [Fact]
        public async Task GetAllParts_Category_PartialValueDoesNotMatch()
        {
            var vendor = await CreateVendorAsync();
            var token = Guid.NewGuid().ToString("N").Substring(0, 8);
            await CreatePartAsync(vendor.Id, "A", category: $"Cat{token}");

            var result = await _repository.GetAllParts(Scoped(category: $"Cat{token.Substring(0, 4)}"));

            Assert.Empty(result);
        }

        [Fact]
        public async Task GetAllParts_LowStockOnlyTrue_ReturnsPartsAtOrBelowReorderLevel()
        {
            var vendor = await CreateVendorAsync();
            var below = await CreatePartAsync(vendor.Id, "A", quantity: 2, reorder: 5);
            var at = await CreatePartAsync(vendor.Id, "B", quantity: 5, reorder: 5);
            await CreatePartAsync(vendor.Id, "C", quantity: 6, reorder: 5);

            var result = await _repository.GetAllParts(Scoped(lowStockOnly: true));

            Assert.Equal(new[] { below.Id, at.Id }, result.Select(p => p.Id).ToArray());
            Assert.Equal(2, result.MetaData.TotalCount);
        }

        [Fact]
        public async Task GetAllParts_LowStockOnlyFalseOrOmitted_ReturnsAllParts()
        {
            var vendor = await CreateVendorAsync();
            await CreatePartAsync(vendor.Id, "A", quantity: 1, reorder: 5);
            await CreatePartAsync(vendor.Id, "B", quantity: 9, reorder: 5);

            var explicitFalse = await _repository.GetAllParts(Scoped(lowStockOnly: false));
            var omitted = await _repository.GetAllParts(Scoped());

            Assert.Equal(2, explicitFalse.MetaData.TotalCount);
            Assert.Equal(2, omitted.MetaData.TotalCount);
        }

        [Fact]
        public async Task GetAllParts_SearchCategoryAndLowStock_AllApplyTogether()
        {
            var vendor = await CreateVendorAsync();
            var token = Guid.NewGuid().ToString("N").Substring(0, 8);
            var hit = await CreatePartAsync(vendor.Id, "A", category: $"Cat{token}", quantity: 1, reorder: 5);
            await CreatePartAsync(vendor.Id, "B", category: $"Cat{token}", quantity: 9, reorder: 5);   // not low
            await CreatePartAsync(vendor.Id, "C", category: "Other", quantity: 1, reorder: 5);         // wrong category
            await CreatePartAsync(vendor.Id, "D", category: $"Cat{token}", quantity: 1, reorder: 5);   // wrong search

            var result = await _repository.GetAllParts(Scoped($"Cat{token}", true, $"{_marker}-A"));

            var only = Assert.Single(result);
            Assert.Equal(hit.Id, only.Id);
            Assert.Equal(1, result.MetaData.TotalCount);
        }

        // ---- GetAllParts: ordering, paging, shape ----

        [Fact]
        public async Task GetAllParts_OrderedByPartNumber()
        {
            var vendor = await CreateVendorAsync();
            var c = await CreatePartAsync(vendor.Id, "C");
            var a = await CreatePartAsync(vendor.Id, "A");
            var b = await CreatePartAsync(vendor.Id, "B");

            var result = await _repository.GetAllParts(Scoped());

            Assert.Equal(new[] { a.Id, b.Id, c.Id }, result.Select(p => p.Id).ToArray());
        }

        [Fact]
        public async Task GetAllParts_Paging_SecondPageAndTotalCountOfAllMatches()
        {
            var vendor = await CreateVendorAsync();
            await CreatePartAsync(vendor.Id, "A");
            var second = await CreatePartAsync(vendor.Id, "B");
            await CreatePartAsync(vendor.Id, "C");

            var page2 = await _repository.GetAllParts(
                new PartParameters { SearchTerm = _marker, PageNumber = 2, PageSize = 1 });

            var only = Assert.Single(page2);
            Assert.Equal(second.Id, only.Id);
            Assert.Equal(3, page2.MetaData.TotalCount);
            Assert.Equal(3, page2.MetaData.TotalPages);
            Assert.Equal(2, page2.MetaData.CurrentPage);
            Assert.Equal(1, page2.MetaData.PageSize);
        }

        [Fact]
        public async Task GetAllParts_NoMatches_ReturnsEmptyPagedList()
        {
            var result = await _repository.GetAllParts(
                new PartParameters { SearchTerm = $"no-such-part-{Guid.NewGuid():N}" });

            Assert.NotNull(result);
            Assert.Empty(result);
            Assert.Equal(0, result.MetaData.TotalCount);
        }

        [Fact]
        public async Task GetAllParts_CalculatedFieldsAreLeftAtDefaults()
        {
            var vendor = await CreateVendorAsync();
            await CreatePartAsync(vendor.Id, "A", cost: 10m, quantity: 1, reorder: 5);

            var only = Assert.Single(await _repository.GetAllParts(Scoped()));

            Assert.Equal(10m, only.Cost_Price);
            Assert.Equal(0m, only.Inventory_Value);
            Assert.False(only.Is_Low_Stock);
        }

        // ---- GetPartById ----

        [Fact]
        public async Task GetPartById_Existing_ReturnsPart()
        {
            var vendor = await CreateVendorAsync();
            var created = await CreatePartAsync(vendor.Id, "A");

            var result = await _repository.GetPartById(created.Id);

            Assert.NotNull(result);
            Assert.Equal(created.Id, result!.Id);
            Assert.Equal(created.Part_Number, result.Part_Number);
            Assert.Equal(vendor.Id, result.Vendor_Id);
        }

        [Fact]
        public async Task GetPartById_Unknown_ReturnsNull()
        {
            Assert.Null(await _repository.GetPartById(-1));
        }

        // ---- UpdatePart (receive stock) ----

        private static PartUpdateDto Receive(int quantityReceived) => new() { Quantity_Received = quantityReceived };

        [Fact]
        public async Task UpdatePart_AddsReceivedAmountToCurrentQuantity()
        {
            var vendor = await CreateVendorAsync();
            var part = await CreatePartAsync(vendor.Id, "A", quantity: 10);

            var updated = await _repository.UpdatePart(part.Id, Receive(5));

            Assert.NotNull(updated);
            Assert.Equal(15, updated!.Quantity);
            Assert.Equal(15, (await _repository.GetPartById(part.Id))!.Quantity);
        }

        [Fact]
        public async Task UpdatePart_ChangesOnlyTheQuantityColumn()
        {
            var vendor = await CreateVendorAsync();
            var part = await CreatePartAsync(vendor.Id, "A", "Brake Pad", "Brakes", 10.25m, 15.75m, 10, 4);

            var updated = await _repository.UpdatePart(part.Id, Receive(5));

            Assert.Equal(part.Part_Number, updated!.Part_Number);
            Assert.Equal("Brake Pad", updated.Name);
            Assert.Equal("Brakes", updated.Category);
            Assert.Equal(vendor.Id, updated.Vendor_Id);
            Assert.Equal(10.25m, updated.Cost_Price);
            Assert.Equal(15.75m, updated.Sell_Price);
            Assert.Equal(4, updated.Reorder_Level);
            Assert.Equal("Main", updated.Warehouse);
        }

        [Fact]
        public async Task UpdatePart_CalculatedFieldsAreNotFilled()
        {
            var vendor = await CreateVendorAsync();
            var part = await CreatePartAsync(vendor.Id, "A", quantity: 10);

            var updated = await _repository.UpdatePart(part.Id, Receive(5));

            // Inventory_Value and Is_Low_Stock are the service's job.
            Assert.Equal(0m, updated!.Inventory_Value);
            Assert.False(updated.Is_Low_Stock);
        }

        [Fact]
        public async Task UpdatePart_RepeatedReceipts_Accumulate()
        {
            var vendor = await CreateVendorAsync();
            var part = await CreatePartAsync(vendor.Id, "A", quantity: 10);

            await _repository.UpdatePart(part.Id, Receive(5));
            var updated = await _repository.UpdatePart(part.Id, Receive(7));

            Assert.Equal(22, updated!.Quantity);
        }

        [Fact]
        public async Task UpdatePart_ConcurrentReceipts_NoneAreLost()
        {
            var vendor = await CreateVendorAsync();
            var part = await CreatePartAsync(vendor.Id, "A", quantity: 10);

            await Task.WhenAll(
                _repository.UpdatePart(part.Id, Receive(3)),
                _repository.UpdatePart(part.Id, Receive(3)),
                _repository.UpdatePart(part.Id, Receive(3)),
                _repository.UpdatePart(part.Id, Receive(3)));

            Assert.Equal(22, (await _repository.GetPartById(part.Id))!.Quantity);
        }

        [Fact]
        public async Task UpdatePart_DoesNotTouchOtherParts()
        {
            var vendor = await CreateVendorAsync();
            var first = await CreatePartAsync(vendor.Id, "A", quantity: 10);
            var second = await CreatePartAsync(vendor.Id, "B", quantity: 10);

            await _repository.UpdatePart(first.Id, Receive(5));

            Assert.Equal(10, (await _repository.GetPartById(second.Id))!.Quantity);
        }

        [Fact]
        public async Task UpdatePart_Unknown_ReturnsNull()
        {
            Assert.Null(await _repository.UpdatePart(-1, Receive(5)));
        }
    }
}
