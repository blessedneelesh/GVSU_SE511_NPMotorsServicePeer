using System.Net;
using System.Text;
using Dapper;
using Newtonsoft.Json;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.Models.Enums;
using NPMotors.Repository.Repositories;

namespace Module.Tests.Controllers
{
    public class VehicleControllerTest : TestBase, IAsyncLifetime
    {
        private VendorRepository _vendorRepo => new(_repositoryContext);
        private VehicleRepository _vehicleRepo => new(_repositoryContext, null!);
        private readonly string _marker = $"itest-{Guid.NewGuid():N}".Substring(0, 12);
        private readonly List<long> _vehicleIds = new();
        private readonly List<long> _vendorIds = new();

        private long _purchaseVendorId;
        private long _bankId;
        private long _accountId;

        public async Task InitializeAsync()
        {
            // Vendor, bank and bank account used by the PurchaseVehicle tests (Story 19).
            var vendor = await _vendorRepo.AddVendor(new VendorCreateDto
            {
                Name = $"{_marker} Vendor",
                Type = VendorType.Vehicles,
                Status = Status.Active
            });
            _purchaseVendorId = vendor.Id;

            using var connection = _repositoryContext.GetDatabaseConnection();
            _bankId = await connection.ExecuteScalarAsync<long>(
                "INSERT INTO bank (name) VALUES (@Name) RETURNING id", new { Name = $"{_marker} Bank" });
            _accountId = await InsertAccountAsync(100000m);
        }

        public async Task DisposeAsync()
        {
            try
            {
                using var connection = _repositoryContext.GetDatabaseConnection();
                var p = new { Marker = _marker + "%", BankId = _bankId };
                await connection.ExecuteAsync(
                    "DELETE FROM bank_payment WHERE vehicle_id IN (SELECT id FROM vehicle WHERE vin ILIKE @Marker)", p);
                await connection.ExecuteAsync(
                    "DELETE FROM ap_bill WHERE vehicle_id IN (SELECT id FROM vehicle WHERE vin ILIKE @Marker)", p);
                await connection.ExecuteAsync(
                    "DELETE FROM bank_statement_line WHERE bank_account_id IN (SELECT id FROM bank_account WHERE bank_id = @BankId)", p);
                await connection.ExecuteAsync(
                    @"DELETE FROM ""transaction"" WHERE bank_account_id IN (SELECT id FROM bank_account WHERE bank_id = @BankId)", p);
                await connection.ExecuteAsync("DELETE FROM vehicle WHERE vin ILIKE @Marker", p);
                await connection.ExecuteAsync("DELETE FROM bank_account WHERE bank_id = @BankId", p);
                await connection.ExecuteAsync("DELETE FROM bank WHERE id = @BankId", p);
                await _vendorRepo.DeleteVendor(_purchaseVendorId);
            }
            catch { }

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

        // ---- Story 19: PurchaseVehicle ----

        private async Task<long> InsertAccountAsync(decimal balance, string status = "active")
        {
            using var connection = _repositoryContext.GetDatabaseConnection();
            return await connection.ExecuteScalarAsync<long>(
                @"INSERT INTO bank_account (bank_id, name, type, opening_balance, book_balance, status)
                  VALUES (@BankId, @Name, 'Checking', @Balance, @Balance, @Status) RETURNING id",
                new { BankId = _bankId, Name = $"{_marker} Account {Guid.NewGuid():N}".Substring(0, 40), Balance = balance, Status = status });
        }

        // Total cost 40 000 (38 000 + 2 000).
        private VehiclePurchaseCreateDto Purchase(string condition, decimal paid, string suffix = "1") => new()
        {
            Vendor_Id = _purchaseVendorId,
            Vin = $"{_marker}{suffix}".ToUpperInvariant().PadRight(17, '0').Substring(0, 17),
            Make = "Honda",
            Model = "Accord",
            Purchase_Date = new DateTime(2026, 11, 3),
            Purchase_Price = 38000m,
            Additional_Costs = 2000m,
            Payment_Condition = condition,
            Amount_Paid = paid,
            Payment_Method = paid > 0 ? "Check" : null,
            Reference = paid > 0 ? "CHK-1001" : null,
            Bank_Account_Id = paid > 0 ? _accountId : null,
            Due_Date = new DateTime(2026, 12, 3)
        };

        private Task<HttpResponseMessage> PostPurchaseAsync(object body) =>
            _testClient.PostAsync("Vehicle/PurchaseVehicle", Json(body));

        private async Task<int> CountAsync(string sql, object? param = null)
        {
            using var connection = _repositoryContext.GetDatabaseConnection();
            return await connection.ExecuteScalarAsync<int>(sql, param);
        }

        private Task<int> VehicleCountAsync() =>
            CountAsync("SELECT COUNT(*) FROM vehicle WHERE vin ILIKE @Marker", new { Marker = _marker + "%" });

        private async Task<decimal> BalanceAsync()
        {
            using var connection = _repositoryContext.GetDatabaseConnection();
            return await connection.ExecuteScalarAsync<decimal>(
                "SELECT book_balance FROM bank_account WHERE id = @Id", new { Id = _accountId });
        }

        // ---- Success ----

        [Fact]
        public async Task PurchaseVehicle_Partial_Returns201WithApBillAndPayment()
        {
            var response = await PostPurchaseAsync(Purchase("Partial", 15000m));
            var body = await ReadAsync<VehiclePurchaseResponseDto>(response);

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            Assert.NotNull(body);
            Assert.True(body!.Id > 0);
            Assert.Equal(40000m, body.Total_Cost);
            Assert.Equal("Partial", body.Payment_Condition);
            Assert.Equal(15000m, body.Amount_Paid);
            Assert.Equal(25000m, body.Balance_Due);
            Assert.Equal("partial", body.Payment_Status);
            Assert.NotNull(body.Ap_Bill_Id);
            Assert.NotNull(body.Ap_Bill_Number);
            Assert.NotNull(body.Bank_Payment_Id);
            Assert.NotNull(body.Payment_Number);
            Assert.NotNull(body.Transaction_Id);
            Assert.Equal(85000m, body.Book_Balance);
            Assert.Equal($"{_marker} Vendor", body.Vendor_Name);
            Assert.Equal(85000m, await BalanceAsync());
        }

        [Fact]
        public async Task PurchaseVehicle_PaidInFull_Returns201WithoutApBill()
        {
            var response = await PostPurchaseAsync(Purchase("PaidInFull", 40000m));
            var body = await ReadAsync<VehiclePurchaseResponseDto>(response);

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            Assert.Null(body!.Ap_Bill_Id);
            Assert.Null(body.Ap_Bill_Number);
            Assert.NotNull(body.Bank_Payment_Id);
            Assert.Equal(0m, body.Balance_Due);
            Assert.Equal("paid", body.Payment_Status);
            Assert.Equal(60000m, await BalanceAsync());
        }

        [Fact]
        public async Task PurchaseVehicle_Credit_Returns201WithApBillAndNoPayment()
        {
            var response = await PostPurchaseAsync(Purchase("Credit", 0m));
            var body = await ReadAsync<VehiclePurchaseResponseDto>(response);

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            Assert.NotNull(body!.Ap_Bill_Id);
            Assert.Null(body.Bank_Payment_Id);
            Assert.Null(body.Transaction_Id);
            Assert.Equal(40000m, body.Balance_Due);
            Assert.Equal("unpaid", body.Payment_Status);
            Assert.Equal(100000m, await BalanceAsync());
        }

        [Fact]
        public async Task PurchaseVehicle_ApBillSavedWithVehicleCostAsAmount()
        {
            var body = await ReadAsync<VehiclePurchaseResponseDto>(await PostPurchaseAsync(Purchase("Partial", 15000m)));

            using var connection = _repositoryContext.GetDatabaseConnection();
            var row = await connection.QuerySingleAsync<(decimal Amount, decimal Paid, decimal Balance, string Status)>(
                "SELECT amount, paid, balance, status FROM ap_bill WHERE id = @Id", new { Id = body!.Ap_Bill_Id });
            Assert.Equal(40000m, row.Amount);
            Assert.Equal(15000m, row.Paid);
            Assert.Equal(25000m, row.Balance);
            Assert.Equal("partial", row.Status);
        }

        // ---- 400: invalid DTO ----

        [Fact]
        public async Task PurchaseVehicle_NegativeAmountPaid_Returns400_NothingSaved()
        {
            var response = await PostPurchaseAsync(Purchase("Partial", -1m));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(0, await VehicleCountAsync());
        }

        [Fact]
        public async Task PurchaseVehicle_UnknownCondition_Returns400()
        {
            var response = await PostPurchaseAsync(Purchase("Layaway", 0m));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(0, await VehicleCountAsync());
        }

        [Fact]
        public async Task PurchaseVehicle_MissingCondition_Returns400()
        {
            var dto = Purchase("Credit", 0m);
            dto.Payment_Condition = "";

            var response = await PostPurchaseAsync(dto);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task PurchaseVehicle_UnknownMethod_Returns400()
        {
            var dto = Purchase("Partial", 15000m);
            dto.Payment_Method = "Bitcoin";

            var response = await PostPurchaseAsync(dto);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(0, await VehicleCountAsync());
        }

        [Fact]
        public async Task PurchaseVehicle_ReferenceOver100Characters_Returns400()
        {
            var dto = Purchase("Partial", 15000m);
            dto.Reference = new string('x', 101);

            var response = await PostPurchaseAsync(dto);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task PurchaseVehicle_EmptyBody_Returns400()
        {
            var response = await _testClient.PostAsync("Vehicle/PurchaseVehicle",
                new StringContent("{}", Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        // ---- 400: business rules ----

        [Fact]
        public async Task PurchaseVehicle_AmountPaidAboveTotalCost_Returns400_NothingSaved()
        {
            var response = await PostPurchaseAsync(Purchase("PaidInFull", 40000.01m));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(0, await VehicleCountAsync());
            Assert.Equal(100000m, await BalanceAsync());
        }

        [Fact]
        public async Task PurchaseVehicle_AmountPaidWithoutBankAccount_Returns400_NothingSaved()
        {
            var dto = Purchase("Partial", 15000m);
            dto.Bank_Account_Id = null;

            var response = await PostPurchaseAsync(dto);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(0, await VehicleCountAsync());
        }

        [Fact]
        public async Task PurchaseVehicle_ConditionDoesNotMatchAmount_Returns400()
        {
            var response = await PostPurchaseAsync(Purchase("Credit", 100m));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(0, await VehicleCountAsync());
        }

        [Fact]
        public async Task PurchaseVehicle_PaymentAboveBookBalance_Returns400_NothingSaved()
        {
            var poor = await InsertAccountAsync(10000m);
            var dto = Purchase("Partial", 15000m);
            dto.Bank_Account_Id = poor;

            var response = await PostPurchaseAsync(dto);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(0, await VehicleCountAsync());
            Assert.Equal(0, await CountAsync(
                @"SELECT COUNT(*) FROM ""transaction"" WHERE bank_account_id = @Id", new { Id = poor }));
            Assert.Equal(0, await CountAsync(
                "SELECT COUNT(*) FROM ap_bill a JOIN vehicle v ON v.id = a.vehicle_id WHERE v.vin ILIKE @Marker",
                new { Marker = _marker + "%" }));
        }

        [Fact]
        public async Task PurchaseVehicle_InactiveBankAccount_Returns400()
        {
            var inactive = await InsertAccountAsync(50000m, "inactive");
            var dto = Purchase("Partial", 15000m);
            dto.Bank_Account_Id = inactive;

            var response = await PostPurchaseAsync(dto);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(0, await VehicleCountAsync());
        }

        [Fact]
        public async Task PurchaseVehicle_DuplicateVin_Returns400()
        {
            Assert.Equal(HttpStatusCode.Created, (await PostPurchaseAsync(Purchase("Credit", 0m))).StatusCode);

            var response = await PostPurchaseAsync(Purchase("Credit", 0m));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(1, await VehicleCountAsync());
        }

        [Fact]
        public async Task PurchaseVehicle_InactiveVendor_Returns400()
        {
            var inactive = await _vendorRepo.AddVendor(new VendorCreateDto
            {
                Name = $"{_marker} Inactive",
                Type = VendorType.Vehicles,
                Status = Status.Inactive
            });
            try
            {
                var dto = Purchase("Credit", 0m);
                dto.Vendor_Id = inactive.Id;

                var response = await PostPurchaseAsync(dto);

                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            }
            finally
            {
                await _vendorRepo.DeleteVendor(inactive.Id);
            }
        }

        // ---- Routing ----

        [Fact]
        public async Task PurchaseVehicle_Get_IsNotAllowed()
        {
            var response = await _testClient.GetAsync("Vehicle/PurchaseVehicle");

            Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        }
    }
}
