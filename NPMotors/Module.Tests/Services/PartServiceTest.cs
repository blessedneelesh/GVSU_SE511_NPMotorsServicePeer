using NPMotors.Repository.Interface;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.RequestFeatures;
using NPMotors.Services.Service;

namespace Module.Tests.Services
{
    public class PartServiceTest
    {
        // Hand-written fake: the test projects do not use a mocking library.
        private class FakePartRepository : IPartRepository
        {
            public PagedList<PartResponseDto> GetAllResult { get; set; } =
                new(new List<PartResponseDto>(), 0, 1, 5);
            public PartResponseDto? GetByIdResult { get; set; }
            public PartResponseDto AddResult { get; set; } = Part(0, 0m, 0);
            public Exception? AddException { get; set; }

            public List<PartParameters> GetAllCalls { get; } = new();
            public List<long> GetByIdCalls { get; } = new();
            public List<PartCreateDto> AddCalls { get; } = new();

            public Task<PagedList<PartResponseDto>> GetAllParts(PartParameters parameters)
            {
                GetAllCalls.Add(parameters);
                return Task.FromResult(GetAllResult);
            }

            public Task<PartResponseDto?> GetPartById(long id)
            {
                GetByIdCalls.Add(id);
                return Task.FromResult(GetByIdResult);
            }

            public PartResponseDto? UpdateResult { get; set; }
            public List<(long Id, PartUpdateDto Part)> UpdateCalls { get; } = new();

            public Task<PartResponseDto?> UpdatePart(long id, PartUpdateDto part)
            {
                UpdateCalls.Add((id, part));
                return Task.FromResult(UpdateResult);
            }

            public Task<PartResponseDto> AddPart(PartCreateDto part)
            {
                AddCalls.Add(part);
                if (AddException != null)
                    throw AddException;
                return Task.FromResult(AddResult);
            }
        }

        private readonly FakePartRepository _repository = new();
        private readonly PartService _service;

        public PartServiceTest()
        {
            _service = new PartService(_repository);
        }

        private static PartResponseDto Part(int quantity, decimal costPrice, int reorderLevel, long id = 1) => new()
        {
            Id = id,
            Part_Number = "PN-" + id,
            Vendor_Id = 7,
            Quantity = quantity,
            Cost_Price = costPrice,
            Sell_Price = costPrice * 2,
            Reorder_Level = reorderLevel
        };

        private async Task<PartResponseDto> CalculatedAsync(PartResponseDto part)
        {
            _repository.GetByIdResult = part;
            var result = await _service.GetPartById(part.Id);
            Assert.NotNull(result);
            return result!;
        }

        private static PartCreateDto ValidCreate() => new()
        {
            Part_Number = "BRK-100",
            Vendor_Id = 7,
            Cost_Price = 10m,
            Sell_Price = 15m,
            Quantity = 4,
            Reorder_Level = 2
        };

        // ---- CalculateInventoryValue via GetPartById ----

        [Fact]
        public async Task GetPartById_InventoryValue_IsQuantityTimesCostPrice()
        {
            var result = await CalculatedAsync(Part(10, 12.50m, 5));

            Assert.Equal(125.00m, result.Inventory_Value);
        }

        [Fact]
        public async Task GetPartById_InventoryValue_AnotherQuantityAndCost()
        {
            var result = await CalculatedAsync(Part(3, 4.25m, 1));

            Assert.Equal(12.75m, result.Inventory_Value);
        }

        [Fact]
        public async Task GetPartById_InventoryValue_ZeroQuantity_IsZero()
        {
            var result = await CalculatedAsync(Part(0, 99.99m, 5));

            Assert.Equal(0m, result.Inventory_Value);
        }

        [Fact]
        public async Task GetPartById_InventoryValue_UsesCostPriceNotSellPrice()
        {
            var part = Part(10, 12.50m, 5);
            part.Sell_Price = 1000m;

            var result = await CalculatedAsync(part);

            Assert.Equal(125.00m, result.Inventory_Value);
        }

        // ---- IsLowStock via GetPartById ----

        [Fact]
        public async Task GetPartById_QuantityEqualsReorderLevel_IsLowStock()
        {
            var result = await CalculatedAsync(Part(5, 1m, 5));

            Assert.True(result.Is_Low_Stock);
        }

        [Fact]
        public async Task GetPartById_QuantityBelowReorderLevel_IsLowStock()
        {
            var result = await CalculatedAsync(Part(4, 1m, 5));

            Assert.True(result.Is_Low_Stock);
        }

        [Fact]
        public async Task GetPartById_QuantityAboveReorderLevel_IsNotLowStock()
        {
            var result = await CalculatedAsync(Part(6, 1m, 5));

            Assert.False(result.Is_Low_Stock);
        }

        [Fact]
        public async Task GetPartById_OneInStockZeroReorderLevel_IsNotLowStock()
        {
            var result = await CalculatedAsync(Part(1, 1m, 0));

            Assert.False(result.Is_Low_Stock);
        }

        // ---- GetPartById ----

        [Fact]
        public async Task GetPartById_NotFound_ReturnsNull()
        {
            _repository.GetByIdResult = null;

            var result = await _service.GetPartById(99);

            Assert.Null(result);
        }

        // ---- GetAllParts ----

        [Fact]
        public async Task GetAllParts_CalculatesEveryItem()
        {
            _repository.GetAllResult = new PagedList<PartResponseDto>(new List<PartResponseDto>
            {
                Part(10, 12.50m, 5, 1),   // 125.00, not low
                Part(2, 30m, 2, 2),       // 60, low (at level)
                Part(0, 8m, 3, 3)         // 0, low
            }, 3, 1, 5);

            var result = await _service.GetAllParts(new PartParameters());

            Assert.Equal(3, result.Count);
            Assert.Equal(125.00m, result[0].Inventory_Value);
            Assert.False(result[0].Is_Low_Stock);
            Assert.Equal(60m, result[1].Inventory_Value);
            Assert.True(result[1].Is_Low_Stock);
            Assert.Equal(0m, result[2].Inventory_Value);
            Assert.True(result[2].Is_Low_Stock);
        }

        [Fact]
        public async Task GetAllParts_KeepsMetaData()
        {
            _repository.GetAllResult = new PagedList<PartResponseDto>(new List<PartResponseDto>
            {
                Part(1, 1m, 1)
            }, 12, 2, 5);

            var result = await _service.GetAllParts(new PartParameters { PageNumber = 2, PageSize = 5 });

            Assert.Equal(12, result.MetaData.TotalCount);
            Assert.Equal(2, result.MetaData.CurrentPage);
            Assert.Equal(5, result.MetaData.PageSize);
            Assert.Equal(3, result.MetaData.TotalPages);
        }

        [Fact]
        public async Task GetAllParts_PageSizeAboveCap_IsCappedAt50()
        {
            await _service.GetAllParts(new PartParameters { PageSize = 500 });

            Assert.Equal(50, Assert.Single(_repository.GetAllCalls).PageSize);
        }

        // ---- AddPart ----

        [Fact]
        public async Task AddPart_ReturnsCreatedPartWithCalculatedFields()
        {
            _repository.AddResult = Part(4, 10m, 2, id: 9);

            var result = await _service.AddPart(ValidCreate());

            Assert.Equal(9, result.Id);
            Assert.Equal(40m, result.Inventory_Value);
            Assert.False(result.Is_Low_Stock);
        }

        [Fact]
        public async Task AddPart_ZeroPricesAndQuantity_AreAccepted()
        {
            var dto = ValidCreate();
            dto.Cost_Price = 0m;
            dto.Sell_Price = 0m;
            dto.Quantity = 0;
            dto.Reorder_Level = 0;

            await _service.AddPart(dto);

            Assert.Single(_repository.AddCalls);
        }

        // ---- Validation (acceptance criteria 1 to 3) ----

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public async Task AddPart_MissingPartNumber_ThrowsAndSkipsRepository(string? partNumber)
        {
            var dto = ValidCreate();
            dto.Part_Number = partNumber!;

            var ex = await Assert.ThrowsAsync<ArgumentException>(() => _service.AddPart(dto));

            Assert.Equal("Part number is required.", ex.Message);
            Assert.Empty(_repository.AddCalls);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public async Task AddPart_MissingVendor_ThrowsAndSkipsRepository(long vendorId)
        {
            var dto = ValidCreate();
            dto.Vendor_Id = vendorId;

            var ex = await Assert.ThrowsAsync<ArgumentException>(() => _service.AddPart(dto));

            Assert.Equal("Vendor is required.", ex.Message);
            Assert.Empty(_repository.AddCalls);
        }

        [Fact]
        public async Task AddPart_NegativeCostPrice_ThrowsAndSkipsRepository()
        {
            var dto = ValidCreate();
            dto.Cost_Price = -0.01m;

            var ex = await Assert.ThrowsAsync<ArgumentException>(() => _service.AddPart(dto));

            Assert.Equal("Cost price cannot be negative.", ex.Message);
            Assert.Empty(_repository.AddCalls);
        }

        [Fact]
        public async Task AddPart_NegativeSellPrice_ThrowsAndSkipsRepository()
        {
            var dto = ValidCreate();
            dto.Sell_Price = -1m;

            var ex = await Assert.ThrowsAsync<ArgumentException>(() => _service.AddPart(dto));

            Assert.Equal("Sell price cannot be negative.", ex.Message);
            Assert.Empty(_repository.AddCalls);
        }

        [Fact]
        public async Task AddPart_NegativeQuantity_ThrowsAndSkipsRepository()
        {
            var dto = ValidCreate();
            dto.Quantity = -1;

            var ex = await Assert.ThrowsAsync<ArgumentException>(() => _service.AddPart(dto));

            Assert.Equal("Quantity cannot be negative.", ex.Message);
            Assert.Empty(_repository.AddCalls);
        }

        [Fact]
        public async Task AddPart_NegativeReorderLevel_ThrowsAndSkipsRepository()
        {
            var dto = ValidCreate();
            dto.Reorder_Level = -1;

            var ex = await Assert.ThrowsAsync<ArgumentException>(() => _service.AddPart(dto));

            Assert.Equal("Reorder level cannot be negative.", ex.Message);
            Assert.Empty(_repository.AddCalls);
        }

        // ---- UpdatePart (receive stock) ----

        private static PartUpdateDto Receive(int? quantityReceived) => new() { Quantity_Received = quantityReceived };

        [Fact]
        public async Task UpdatePart_InventoryValue_IsNewQuantityTimesCostPrice()
        {
            _repository.UpdateResult = Part(15, 4m, 5);

            var result = await _service.UpdatePart(1, Receive(5));

            Assert.Equal(60m, result!.Inventory_Value);
        }

        [Theory]
        [InlineData(5, 5, true)]    // boundary: quantity == reorder level
        [InlineData(4, 5, true)]
        [InlineData(6, 5, false)]
        public async Task UpdatePart_IsLowStock_UsesQuantityAtOrBelowReorderLevel(int quantity, int reorderLevel, bool expected)
        {
            _repository.UpdateResult = Part(quantity, 1m, reorderLevel);

            var result = await _service.UpdatePart(1, Receive(1));

            Assert.Equal(expected, result!.Is_Low_Stock);
        }

        [Fact]
        public async Task UpdatePart_ReturnsRepositoryPart()
        {
            _repository.UpdateResult = Part(15, 4m, 5, id: 9);

            var result = await _service.UpdatePart(9, Receive(5));

            Assert.Same(_repository.UpdateResult, result);
            Assert.Equal(15, result!.Quantity);
        }

        [Fact]
        public async Task UpdatePart_NotFound_ReturnsNull()
        {
            _repository.UpdateResult = null;

            Assert.Null(await _service.UpdatePart(99, Receive(5)));
        }

        [Fact]
        public async Task UpdatePart_PassesIdAndDtoToRepositoryUnchanged()
        {
            _repository.UpdateResult = Part(15, 4m, 5, id: 42);
            var dto = Receive(5);

            await _service.UpdatePart(42, dto);

            var call = Assert.Single(_repository.UpdateCalls);
            Assert.Equal(42, call.Id);
            Assert.Same(dto, call.Part);
        }

        [Fact]
        public async Task UpdatePart_MissingQuantityReceived_ThrowsAndSkipsRepository()
        {
            var ex = await Assert.ThrowsAsync<ArgumentException>(() => _service.UpdatePart(1, Receive(null)));

            Assert.Equal("Quantity received is required.", ex.Message);
            Assert.Empty(_repository.UpdateCalls);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public async Task UpdatePart_ZeroOrNegativeQuantityReceived_ThrowsAndSkipsRepository(int quantityReceived)
        {
            var ex = await Assert.ThrowsAsync<ArgumentException>(() => _service.UpdatePart(1, Receive(quantityReceived)));

            Assert.Equal("Quantity received must be greater than zero.", ex.Message);
            Assert.Empty(_repository.UpdateCalls);
        }

        [Fact]
        public async Task UpdatePart_QuantityReceivedOfOne_IsAccepted()
        {
            _repository.UpdateResult = Part(11, 1m, 5);

            await _service.UpdatePart(1, Receive(1));

            Assert.Single(_repository.UpdateCalls);
        }
    }
}
