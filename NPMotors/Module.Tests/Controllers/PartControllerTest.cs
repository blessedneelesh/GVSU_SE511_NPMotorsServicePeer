using System.Data;
using System.Net;
using System.Text;
using Dapper;
using Newtonsoft.Json;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.Models.Enums;
using NPMotors.Repository.Repositories;

namespace Module.Tests.Controllers
{
    public class PartControllerTest : TestBase, IAsyncLifetime
    {
        private PartRepository _repo => new(_repositoryContext);
        private VendorRepository _vendorRepo => new(_repositoryContext);
        private readonly string _marker = $"itest-{Guid.NewGuid():N}".Substring(0, 18);
        private readonly List<long> _createdIds = new();
        private readonly List<long> _vendorIds = new();

        private class GetAllResponse
        {
            public List<PartResponseDto> Items { get; set; } = new();
        }

        public Task InitializeAsync() => Task.CompletedTask;

        public async Task DisposeAsync()
        {
            // Parts first: they reference the vendors. The repository has no delete, so use SQL.
            foreach (var id in _createdIds)
            {
                try
                {
                    using IDbConnection connection = _repositoryContext.GetDatabaseConnection();
                    await connection.ExecuteAsync("DELETE FROM part WHERE id = @Id", new { Id = id });
                }
                catch
                {
                    // Keep going so one failed delete does not block the rest.
                }
            }

            foreach (var id in _vendorIds)
            {
                try
                {
                    await _vendorRepo.DeleteVendor(id);
                }
                catch
                {
                    // Keep going so one failed delete does not block the rest.
                }
            }
        }

        private async Task<VendorResponseDto> CreateVendorAsync(Status status = Status.Active)
        {
            var vendor = await _vendorRepo.AddVendor(new VendorCreateDto
            {
                Name = $"{_marker} Vendor",
                Type = VendorType.Parts,
                Status = status
            });
            _vendorIds.Add(vendor.Id);
            return vendor;
        }

        private PartCreateDto NewPartDto(long vendorId, string suffix) => new()
        {
            Part_Number = $"{_marker}-{suffix}",
            Name = $"Test{suffix}",
            Category = $"{_marker}-cat",
            Vendor_Id = vendorId,
            Cost_Price = 12.50m,
            Sell_Price = 20m,
            Quantity = 10,
            Reorder_Level = 5,
            Warehouse = "Main"
        };

        private async Task<PartResponseDto> CreatePartAsync(long vendorId, string suffix = "a", Action<PartCreateDto>? customize = null)
        {
            var dto = NewPartDto(vendorId, suffix);
            customize?.Invoke(dto);
            var created = await _repo.AddPart(dto);
            _createdIds.Add(created.Id);
            return created;
        }

        private static StringContent Json(object? body) =>
            new(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");

        private static async Task<T?> ReadAsync<T>(HttpResponseMessage response) =>
            JsonConvert.DeserializeObject<T>(await response.Content.ReadAsStringAsync());

        [Fact]
        public async Task GetAllParts_Returns200_AndContainsInsertedPart()
        {
            // Arrange
            var vendor = await CreateVendorAsync();
            var created = await CreatePartAsync(vendor.Id);

            // Act
            var response = await _testClient.GetAsync($"Part/GetAllParts?SearchTerm={_marker}");
            var body = await ReadAsync<GetAllResponse>(response);

            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.NotNull(body);
            Assert.Contains(body!.Items, p => p.Id == created.Id);
        }

        [Fact]
        public async Task GetAllParts_ReturnsCalculatedFields()
        {
            // Arrange
            var vendor = await CreateVendorAsync();
            await CreatePartAsync(vendor.Id, "stocked");
            await CreatePartAsync(vendor.Id, "low", d => d.Quantity = 5);

            // Act
            var response = await _testClient.GetAsync($"Part/GetAllParts?SearchTerm={_marker}");
            var body = await ReadAsync<GetAllResponse>(response);

            // Assert
            var stocked = Assert.Single(body!.Items, p => p.Part_Number.EndsWith("-stocked"));
            Assert.Equal(125m, stocked.Inventory_Value);
            Assert.False(stocked.Is_Low_Stock);
            var low = Assert.Single(body.Items, p => p.Part_Number.EndsWith("-low"));
            Assert.Equal(62.50m, low.Inventory_Value);
            Assert.True(low.Is_Low_Stock);
        }

        [Fact]
        public async Task GetAllParts_FiltersByCategoryAndLowStockOnly()
        {
            // Arrange
            var vendor = await CreateVendorAsync();
            var lowBrakes = await CreatePartAsync(vendor.Id, "lowbrakes", d =>
            {
                d.Category = $"{_marker}-brakes";
                d.Quantity = 1;
            });
            var stockedBrakes = await CreatePartAsync(vendor.Id, "stockedbrakes", d => d.Category = $"{_marker}-brakes");
            var lowFilters = await CreatePartAsync(vendor.Id, "lowfilters", d =>
            {
                d.Category = $"{_marker}-filters";
                d.Quantity = 1;
            });

            // Act
            var byCategory = await ReadAsync<GetAllResponse>(
                await _testClient.GetAsync($"Part/GetAllParts?SearchTerm={_marker}&Category={_marker}-brakes"));
            var byLowStock = await ReadAsync<GetAllResponse>(
                await _testClient.GetAsync($"Part/GetAllParts?SearchTerm={_marker}&LowStockOnly=true"));
            var both = await ReadAsync<GetAllResponse>(
                await _testClient.GetAsync($"Part/GetAllParts?SearchTerm={_marker}&Category={_marker}-brakes&LowStockOnly=true"));

            // Assert
            Assert.Equal(2, byCategory!.Items.Count);
            Assert.DoesNotContain(byCategory.Items, p => p.Id == lowFilters.Id);
            Assert.Equal(2, byLowStock!.Items.Count);
            Assert.DoesNotContain(byLowStock.Items, p => p.Id == stockedBrakes.Id);
            Assert.Single(both!.Items);
            Assert.Equal(lowBrakes.Id, both.Items[0].Id);
        }

        [Fact]
        public async Task GetAllParts_ReturnsPagingMetaData()
        {
            // Arrange
            var vendor = await CreateVendorAsync();
            await CreatePartAsync(vendor.Id, "one");
            await CreatePartAsync(vendor.Id, "two");

            // Act
            var response = await _testClient.GetAsync($"Part/GetAllParts?SearchTerm={_marker}&PageNumber=2&PageSize=1");
            var body = await ReadAsync<GetAllResponse>(response);
            var json = await response.Content.ReadAsStringAsync();

            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Single(body!.Items);
            Assert.Contains("\"metaData\"", json);
            Assert.Contains("\"totalCount\":2", json);
        }

        [Fact]
        public async Task GetAllParts_CapsPageSizeAt50()
        {
            // Act
            var response = await _testClient.GetAsync("Part/GetAllParts?PageSize=500");
            var json = await response.Content.ReadAsStringAsync();

            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("\"pageSize\":50", json);
        }

        [Fact]
        public async Task GetPartById_Returns200_WithMatchingFields()
        {
            // Arrange
            var vendor = await CreateVendorAsync();
            var created = await CreatePartAsync(vendor.Id, "a", d =>
            {
                d.Quantity = 5;
                d.Reorder_Level = 5;
            });

            // Act
            var response = await _testClient.GetAsync($"Part/GetPartById?id={created.Id}");
            var body = await ReadAsync<PartResponseDto>(response);

            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.NotNull(body);
            Assert.Equal(created.Id, body!.Id);
            Assert.Equal(created.Part_Number, body.Part_Number);
            Assert.Equal(vendor.Id, body.Vendor_Id);
            Assert.Equal(62.50m, body.Inventory_Value);
            Assert.True(body.Is_Low_Stock);
        }

        [Fact]
        public async Task GetPartById_Returns404_WhenNotFound()
        {
            // Act
            var response = await _testClient.GetAsync("Part/GetPartById?id=-1");

            // Assert
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        [Fact]
        public async Task GetPartById_Returns400_WhenIdIsNotNumeric()
        {
            // Act
            var response = await _testClient.GetAsync("Part/GetPartById?id=abc");

            // Assert
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task AddPart_Returns201_AndPersistsPart()
        {
            // Arrange
            var vendor = await CreateVendorAsync();
            var dto = NewPartDto(vendor.Id, "post");

            // Act
            var response = await _testClient.PostAsync("Part/AddPart", Json(dto));
            var body = await ReadAsync<PartResponseDto>(response);
            if (body != null && body.Id > 0)
                _createdIds.Add(body.Id);

            // Assert
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            Assert.NotNull(body);
            Assert.True(body!.Id > 0);
            Assert.Equal(125m, body.Inventory_Value);
            Assert.False(body.Is_Low_Stock);
            var reloaded = await _repo.GetPartById(body.Id);
            Assert.NotNull(reloaded);
            Assert.Equal(dto.Part_Number, reloaded!.Part_Number);
            Assert.Equal(vendor.Id, reloaded.Vendor_Id);
            Assert.Equal(dto.Cost_Price, reloaded.Cost_Price);
            Assert.Equal(dto.Sell_Price, reloaded.Sell_Price);
        }

        [Fact]
        public async Task AddPart_Returns201_WhenPartNumberIsAlreadyUsed()
        {
            // Arrange
            var vendor = await CreateVendorAsync();
            var first = await CreatePartAsync(vendor.Id, "dup");
            var dto = NewPartDto(vendor.Id, "dup");

            // Act
            var response = await _testClient.PostAsync("Part/AddPart", Json(dto));
            var body = await ReadAsync<PartResponseDto>(response);
            if (body != null && body.Id > 0)
                _createdIds.Add(body.Id);

            // Assert
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            Assert.NotEqual(first.Id, body!.Id);
        }

        [Fact]
        public async Task AddPart_Returns400_WhenBodyIsNull()
        {
            // Act
            var response = await _testClient.PostAsync("Part/AddPart", Json(null));

            // Assert
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task AddPart_Returns400_WhenPartNumberIsMissing()
        {
            // Arrange
            var vendor = await CreateVendorAsync();
            var missing = NewPartDto(vendor.Id, "missing");
            missing.Part_Number = null!;
            var blank = NewPartDto(vendor.Id, "blank");
            blank.Part_Number = "   ";
            var tooLong = NewPartDto(vendor.Id, "long");
            tooLong.Part_Number = new string('X', 51);

            // Act
            var missingResponse = await _testClient.PostAsync("Part/AddPart", Json(missing));
            var blankResponse = await _testClient.PostAsync("Part/AddPart", Json(blank));
            var tooLongResponse = await _testClient.PostAsync("Part/AddPart", Json(tooLong));

            // Assert
            Assert.Equal(HttpStatusCode.BadRequest, missingResponse.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, blankResponse.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, tooLongResponse.StatusCode);
        }

        [Fact]
        public async Task AddPart_Returns400_WhenVendorIsMissingUnknownOrInactive()
        {
            // Arrange
            var inactive = await CreateVendorAsync(Status.Inactive);
            var active = await CreateVendorAsync();
            var missing = NewPartDto(active.Id, "novendor");
            missing.Vendor_Id = 0;
            var unknown = NewPartDto(-1, "unknown");
            var inactiveVendor = NewPartDto(inactive.Id, "inactive");

            // Act
            var missingResponse = await _testClient.PostAsync("Part/AddPart", Json(missing));
            var unknownResponse = await _testClient.PostAsync("Part/AddPart", Json(unknown));
            var inactiveResponse = await _testClient.PostAsync("Part/AddPart", Json(inactiveVendor));

            // Assert
            Assert.Equal(HttpStatusCode.BadRequest, missingResponse.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, unknownResponse.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, inactiveResponse.StatusCode);
        }

        [Fact]
        public async Task AddPart_Returns400_WhenPricesOrQuantityAreNegative()
        {
            // Arrange
            var vendor = await CreateVendorAsync();
            var negativeCost = NewPartDto(vendor.Id, "negcost");
            negativeCost.Cost_Price = -1m;
            var negativeSell = NewPartDto(vendor.Id, "negsell");
            negativeSell.Sell_Price = -1m;
            var negativeQuantity = NewPartDto(vendor.Id, "negqty");
            negativeQuantity.Quantity = -1;
            var negativeReorder = NewPartDto(vendor.Id, "negreorder");
            negativeReorder.Reorder_Level = -1;

            // Act
            var costResponse = await _testClient.PostAsync("Part/AddPart", Json(negativeCost));
            var sellResponse = await _testClient.PostAsync("Part/AddPart", Json(negativeSell));
            var quantityResponse = await _testClient.PostAsync("Part/AddPart", Json(negativeQuantity));
            var reorderResponse = await _testClient.PostAsync("Part/AddPart", Json(negativeReorder));

            // Assert
            Assert.Equal(HttpStatusCode.BadRequest, costResponse.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, sellResponse.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, quantityResponse.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, reorderResponse.StatusCode);
        }

        [Fact]
        public async Task DeletePart_IsNotMapped()
        {
            // Act
            var deleteResponse = await _testClient.DeleteAsync("Part/DeletePart?id=1");

            // Assert
            Assert.Contains(deleteResponse.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
        }

        // ---- UpdatePart (receive stock) ----

        private Task<HttpResponseMessage> ReceiveAsync(string id, object? body) =>
            _testClient.PutAsync($"Part/UpdatePart?id={id}", Json(body));

        private async Task<int> QuantityOfAsync(long id) => (await _repo.GetPartById(id))!.Quantity;

        [Fact]
        public async Task UpdatePart_Valid_Returns200WithIncreasedQuantityAndCalculatedFields()
        {
            var vendor = await CreateVendorAsync();
            var part = await CreatePartAsync(vendor.Id); // quantity 10, cost 12.50, reorder 5

            var response = await ReceiveAsync(part.Id.ToString(), new { Quantity_Received = 6 });
            var body = await ReadAsync<PartResponseDto>(response);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(part.Id, body!.Id);
            Assert.Equal(16, body.Quantity);
            Assert.Equal(200m, body.Inventory_Value);   // 16 x 12.50
            Assert.False(body.Is_Low_Stock);            // 16 > 5
        }

        [Fact]
        public async Task UpdatePart_LowStockPartReceivingEnough_IsNoLongerLowStock()
        {
            var vendor = await CreateVendorAsync();
            var part = await CreatePartAsync(vendor.Id, "a", p => { p.Quantity = 2; p.Reorder_Level = 5; });

            var response = await ReceiveAsync(part.Id.ToString(), new { Quantity_Received = 4 });
            var body = await ReadAsync<PartResponseDto>(response);

            Assert.Equal(6, body!.Quantity);
            Assert.False(body.Is_Low_Stock);            // 6 > 5
        }

        [Fact]
        public async Task UpdatePart_TwoReceipts_Accumulate()
        {
            var vendor = await CreateVendorAsync();
            var part = await CreatePartAsync(vendor.Id);

            await ReceiveAsync(part.Id.ToString(), new { Quantity_Received = 5 });
            var response = await ReceiveAsync(part.Id.ToString(), new { Quantity_Received = 7 });
            var body = await ReadAsync<PartResponseDto>(response);

            Assert.Equal(22, body!.Quantity);
        }

        [Fact]
        public async Task UpdatePart_IgnoresOtherFieldsInBody()
        {
            var vendor = await CreateVendorAsync();
            var part = await CreatePartAsync(vendor.Id);

            var response = await ReceiveAsync(part.Id.ToString(),
                new { Quantity_Received = 5, Quantity = 999, Cost_Price = 1, Name = "Changed" });
            var body = await ReadAsync<PartResponseDto>(response);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(15, body!.Quantity);
            Assert.Equal(12.50m, body.Cost_Price);
            Assert.Equal(part.Name, body.Name);
        }

        [Fact]
        public async Task UpdatePart_UnknownId_Returns404()
        {
            var response = await ReceiveAsync(long.MaxValue.ToString(), new { Quantity_Received = 5 });

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-3)]
        public async Task UpdatePart_ZeroOrNegativeQuantityReceived_Returns400AndQuantityUnchanged(int quantityReceived)
        {
            var vendor = await CreateVendorAsync();
            var part = await CreatePartAsync(vendor.Id);

            var response = await ReceiveAsync(part.Id.ToString(), new { Quantity_Received = quantityReceived });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(10, await QuantityOfAsync(part.Id));
        }

        [Fact]
        public async Task UpdatePart_MissingQuantityReceived_Returns400AndQuantityUnchanged()
        {
            var vendor = await CreateVendorAsync();
            var part = await CreatePartAsync(vendor.Id);

            var response = await ReceiveAsync(part.Id.ToString(), new { });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(10, await QuantityOfAsync(part.Id));
        }

        [Fact]
        public async Task UpdatePart_NonNumericId_Returns400()
        {
            var response = await ReceiveAsync("abc", new { Quantity_Received = 5 });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task UpdatePart_NonJsonBody_Returns400()
        {
            var response = await _testClient.PutAsync("Part/UpdatePart?id=1",
                new StringContent("not json", Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }
}
