using NPMotors.Repository.Interface;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.Models.Enums;
using NPMotors.Repository.RequestFeatures;
using NPMotors.Services.Service;

namespace Module.Tests.Services
{
    public class VehicleServiceTest
    {
        // Hand-written fake: the test projects do not use a mocking library.
        private class FakeVehicleRepository : IVehicleRepository
        {
            public PagedList<VehicleResponseDto> GetAllResult { get; set; } =
                new(new List<VehicleResponseDto>(), 0, 1, 5);
            public VehicleResponseDto? GetByIdResult { get; set; }

            public List<VehicleParameters> GetAllCalls { get; } = new();
            public List<long> GetByIdCalls { get; } = new();

            public bool VinTaken { get; set; }
            public string? VendorStatus { get; set; } = "active";
            public Exception? PurchaseException { get; set; }
            public List<(VehiclePurchaseCreateDto Purchase, NPMotors.Repository.Models.APBill? ApBill)> PurchaseCalls { get; } = new();
            public List<string> VinChecks { get; } = new();

            public Task<bool> VinExists(string vin)
            {
                VinChecks.Add(vin);
                return Task.FromResult(VinTaken);
            }

            public Task<PagedList<VehicleResponseDto>> GetAllVehicles(VehicleParameters parameters)
            {
                GetAllCalls.Add(parameters);
                return Task.FromResult(GetAllResult);
            }

            // Story 14 member: not used by these tests (covered by implement-story-test).
            public Task<int> MarkVehicleSold(long vehicleId, long invoiceId, System.Data.IDbTransaction tx) =>
                throw new NotImplementedException();

            public Task<VehicleResponseDto?> GetVehicleById(long id)
            {
                GetByIdCalls.Add(id);
                return Task.FromResult(GetByIdResult);
            }

            public Task<VehicleResponseDto> AddVehicle(VehicleCreateDto vehicle) => throw new NotSupportedException();
            public Task<VehiclePurchaseResponseDto> PurchaseVehicle(VehiclePurchaseCreateDto purchase, NPMotors.Repository.Models.APBill? apBill)
            {
                PurchaseCalls.Add((purchase, apBill));
                if (PurchaseException is not null)
                    throw PurchaseException;

                // Like the real repository: total_cost is stored from the vehicle prices only.
                return Task.FromResult(new VehiclePurchaseResponseDto
                {
                    Id = 1,
                    Vendor_Id = purchase.Vendor_Id,
                    Vin = purchase.Vin,
                    Purchase_Price = purchase.Purchase_Price,
                    Additional_Costs = purchase.Additional_Costs ?? 0m,
                    Total_Cost = purchase.Purchase_Price + (purchase.Additional_Costs ?? 0m),
                    Payment_Condition = purchase.Payment_Condition,
                    Amount_Paid = purchase.Amount_Paid
                });
            }
            public Task<string?> GetVendorStatus(long vendorId) => Task.FromResult(VendorStatus);
            public Task<bool> DeleteVehicle(long id) => throw new NotSupportedException();
        }

        private readonly FakeVehicleRepository _repository = new();
        private readonly VehicleService _service;

        public VehicleServiceTest()
        {
            _service = new VehicleService(_repository);
        }

        private static VehicleResponseDto Vehicle(
            decimal purchase, decimal additional, decimal selling,
            VehicleStatus status = VehicleStatus.Available, long id = 1) => new()
        {
            Id = id,
            Vendor_Id = 7,
            Vendor_Name = "Test Vendor",
            Vin = "VIN" + id,
            Purchase_Price = purchase,
            Additional_Costs = additional,
            Selling_Price = selling,
            Status = status
        };

        private async Task<VehicleResponseDto> CalculatedAsync(VehicleResponseDto vehicle)
        {
            _repository.GetByIdResult = vehicle;
            var result = await _service.GetVehicleById(vehicle.Id);
            Assert.NotNull(result);
            return result!;
        }

        // ---- CalculateProfit via GetVehicleById ----

        [Fact]
        public async Task GetVehicleById_CalculatesTotalCost()
        {
            var result = await CalculatedAsync(Vehicle(10000m, 1000m, 14000m));

            Assert.Equal(11000m, result.Total_Cost);
        }

        [Fact]
        public async Task GetVehicleById_GrossProfitUsesTotalCostNotPurchasePrice()
        {
            var result = await CalculatedAsync(Vehicle(10000m, 1000m, 14000m));

            Assert.Equal(3000m, result.Gross_Profit);
            Assert.NotEqual(4000m, result.Gross_Profit);
        }

        [Fact]
        public async Task GetVehicleById_CalculatesGrossMargin()
        {
            var result = await CalculatedAsync(Vehicle(10000m, 1000m, 14000m));

            // 3000 / 14000 * 100 = 21.428571...
            Assert.NotNull(result.Gross_Margin);
            Assert.Equal(21.4285714286m, result.Gross_Margin!.Value, 8);
        }

        [Fact]
        public async Task GetVehicleById_AdditionalCostsChangeProfit()
        {
            var without = await CalculatedAsync(Vehicle(10000m, 0m, 14000m));
            var with = await CalculatedAsync(Vehicle(10000m, 1000m, 14000m));

            Assert.Equal(4000m, without.Gross_Profit);
            Assert.Equal(3000m, with.Gross_Profit);
        }

        [Fact]
        public async Task GetVehicleById_ZeroAdditionalCosts_TotalCostEqualsPurchasePrice()
        {
            var result = await CalculatedAsync(Vehicle(8000m, 0m, 10000m));

            Assert.Equal(8000m, result.Total_Cost);
            Assert.Equal(2000m, result.Gross_Profit);
            Assert.Equal(20m, result.Gross_Margin);
        }

        [Fact]
        public async Task GetVehicleById_SellingPriceZero_MarginNullAndProfitIsNegativeTotalCost()
        {
            var result = await CalculatedAsync(Vehicle(10000m, 500m, 0m));

            Assert.Null(result.Gross_Margin);
            Assert.Equal(-10500m, result.Gross_Profit);
        }

        [Fact]
        public async Task GetVehicleById_SellingBelowCost_NegativeProfitAndMarginNotClamped()
        {
            var result = await CalculatedAsync(Vehicle(10000m, 3000m, 12000m));

            Assert.Equal(13000m, result.Total_Cost);
            Assert.Equal(-1000m, result.Gross_Profit);
            // -1000 / 12000 * 100 = -8.3333...
            Assert.Equal(-8.3333333333m, result.Gross_Margin!.Value, 8);
        }

        [Fact]
        public async Task GetVehicleById_SellingEqualsCost_ZeroProfitZeroMargin()
        {
            var result = await CalculatedAsync(Vehicle(9000m, 1000m, 10000m));

            Assert.Equal(0m, result.Gross_Profit);
            Assert.Equal(0m, result.Gross_Margin);
        }

        [Fact]
        public async Task GetVehicleById_StatusAvailable_IsAvailableTrue()
        {
            var result = await CalculatedAsync(Vehicle(1000m, 0m, 2000m, VehicleStatus.Available));

            Assert.True(result.Is_Available);
            Assert.Equal(VehicleStatus.Available, result.Status);
        }

        [Fact]
        public async Task GetVehicleById_StatusReserved_IsAvailableFalse()
        {
            var result = await CalculatedAsync(Vehicle(1000m, 0m, 2000m, VehicleStatus.Reserved));

            Assert.False(result.Is_Available);
            Assert.Equal(VehicleStatus.Reserved, result.Status);
        }

        [Fact]
        public async Task GetVehicleById_StatusSold_IsAvailableFalse()
        {
            var result = await CalculatedAsync(Vehicle(1000m, 0m, 2000m, VehicleStatus.Sold));

            Assert.False(result.Is_Available);
        }

        // ---- GetVehicleById ----

        [Fact]
        public async Task GetVehicleById_NotFound_ReturnsNull()
        {
            _repository.GetByIdResult = null;

            var result = await _service.GetVehicleById(99);

            Assert.Null(result);
        }

        // ---- GetAllVehicles ----

        [Fact]
        public async Task GetAllVehicles_CalculatesEveryItem()
        {
            _repository.GetAllResult = new PagedList<VehicleResponseDto>(new List<VehicleResponseDto>
            {
                Vehicle(10000m, 1000m, 14000m, VehicleStatus.Available, 1),
                Vehicle(20000m, 0m, 20000m, VehicleStatus.Reserved, 2),
                Vehicle(5000m, 500m, 0m, VehicleStatus.Available, 3)
            }, 3, 1, 5);

            var result = await _service.GetAllVehicles(new VehicleParameters());

            Assert.Equal(3, result.Count);

            Assert.Equal(11000m, result[0].Total_Cost);
            Assert.Equal(3000m, result[0].Gross_Profit);
            Assert.Equal(21.4285714286m, result[0].Gross_Margin!.Value, 8);
            Assert.True(result[0].Is_Available);

            Assert.Equal(20000m, result[1].Total_Cost);
            Assert.Equal(0m, result[1].Gross_Profit);
            Assert.Equal(0m, result[1].Gross_Margin);
            Assert.False(result[1].Is_Available);

            Assert.Equal(5500m, result[2].Total_Cost);
            Assert.Equal(-5500m, result[2].Gross_Profit);
            Assert.Null(result[2].Gross_Margin);
            Assert.True(result[2].Is_Available);
        }

        [Fact]
        public async Task GetAllVehicles_KeepsMetaData()
        {
            _repository.GetAllResult = new PagedList<VehicleResponseDto>(new List<VehicleResponseDto>
            {
                Vehicle(1000m, 0m, 2000m, id: 1)
            }, 12, 2, 5);

            var result = await _service.GetAllVehicles(new VehicleParameters { PageNumber = 2, PageSize = 5 });

            Assert.Equal(12, result.MetaData.TotalCount);
            Assert.Equal(2, result.MetaData.CurrentPage);
            Assert.Equal(5, result.MetaData.PageSize);
            Assert.Equal(3, result.MetaData.TotalPages);
        }

        // ---- Status validation ----

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("available")]
        [InlineData("reserved")]
        [InlineData("Available")]
        [InlineData("RESERVED")]
        public async Task GetAllVehicles_AllowedStatus_CallsRepository(string? status)
        {
            await _service.GetAllVehicles(new VehicleParameters { Status = status });

            Assert.Single(_repository.GetAllCalls);
        }

        [Theory]
        [InlineData("sold")]
        [InlineData("bogus")]
        public async Task GetAllVehicles_UnsupportedStatus_ThrowsAndSkipsRepository(string status)
        {
            await Assert.ThrowsAsync<ArgumentException>(
                () => _service.GetAllVehicles(new VehicleParameters { Status = status }));

            Assert.Empty(_repository.GetAllCalls);
        }

        // ---- Page size cap ----

        [Fact]
        public async Task GetAllVehicles_PageSizeAboveCap_IsCappedAt50()
        {
            var parameters = new VehicleParameters { PageSize = 500 };

            await _service.GetAllVehicles(parameters);

            Assert.Equal(50, Assert.Single(_repository.GetAllCalls).PageSize);
        }

        // ---- Story 19: PurchaseVehicle ----

        // Total cost is 40 000 (38 000 + 2 000) unless a test says otherwise.
        private static VehiclePurchaseCreateDto Purchase(
            string condition, decimal paid, decimal price = 38000m, decimal? additional = 2000m,
            string? method = "Check", long? bankAccountId = 5) => new()
        {
            Vendor_Id = 7,
            Vin = "1HGCM82633A004352",
            Make = "Honda",
            Model = "Accord",
            Purchase_Date = new DateTime(2026, 11, 3),
            Purchase_Price = price,
            Additional_Costs = additional,
            Payment_Condition = condition,
            Amount_Paid = paid,
            Payment_Method = method,
            Bank_Account_Id = bankAccountId,
            Reference = "CHK-1001",
            Due_Date = new DateTime(2026, 12, 3)
        };

        private void AssertNothingSaved() => Assert.Empty(_repository.PurchaseCalls);

        // ---- Public method: story example and conditions ----

        [Fact]
        public async Task PurchaseVehicle_PartialPayment_StoryExample_CreatesApBillAndStatusPartial()
        {
            var result = await _service.PurchaseVehicle(Purchase("Partial", 15000m));

            var (_, apBill) = Assert.Single(_repository.PurchaseCalls);
            Assert.NotNull(apBill);
            Assert.Equal(40000m, apBill!.Amount);
            Assert.Equal(15000m, apBill.Paid);
            Assert.Equal(25000m, apBill.Balance);
            Assert.Equal("partial", apBill.Status);
            Assert.Equal(25000m, result.Balance_Due);
            Assert.Equal("partial", result.Payment_Status);
            Assert.Equal(40000m, result.Total_Cost);
        }

        [Fact]
        public async Task PurchaseVehicle_PaidInFull_NoApBill_StatusPaid_NoBalanceDue()
        {
            var result = await _service.PurchaseVehicle(Purchase("PaidInFull", 40000m));

            var (_, apBill) = Assert.Single(_repository.PurchaseCalls);
            Assert.Null(apBill);
            Assert.Equal("paid", result.Payment_Status);
            Assert.Equal(0m, result.Balance_Due);
            Assert.Equal(40000m, result.Amount_Paid);
        }

        [Fact]
        public async Task PurchaseVehicle_Credit_CreatesApBillForFullCost_StatusUnpaid()
        {
            var result = await _service.PurchaseVehicle(Purchase("Credit", 0m, method: null, bankAccountId: null));

            var (_, apBill) = Assert.Single(_repository.PurchaseCalls);
            Assert.NotNull(apBill);
            Assert.Equal(40000m, apBill!.Amount);
            Assert.Equal(0m, apBill.Paid);
            Assert.Equal(40000m, apBill.Balance);
            Assert.Equal("unpaid", apBill.Status);
            Assert.Equal(40000m, result.Balance_Due);
            Assert.Equal("unpaid", result.Payment_Status);
        }

        [Fact]
        public async Task PurchaseVehicle_ApBillAmountEqualsTotalCost_NotThePurchasePrice()
        {
            await _service.PurchaseVehicle(Purchase("Partial", 1000m, price: 10000m, additional: 1500m));

            Assert.Equal(11500m, _repository.PurchaseCalls[0].ApBill!.Amount);
            Assert.Equal(10500m, _repository.PurchaseCalls[0].ApBill!.Balance);
        }

        [Fact]
        public async Task PurchaseVehicle_NullAdditionalCosts_TotalCostIsPurchasePrice()
        {
            var result = await _service.PurchaseVehicle(Purchase("Credit", 0m, price: 9000m, additional: null, method: null, bankAccountId: null));

            Assert.Equal(9000m, _repository.PurchaseCalls[0].ApBill!.Amount);
            Assert.Equal(9000m, result.Balance_Due);
        }

        [Fact]
        public async Task PurchaseVehicle_ApBillCarriesVendorPurchaseDateAndDueDate()
        {
            await _service.PurchaseVehicle(Purchase("Partial", 15000m));

            var apBill = _repository.PurchaseCalls[0].ApBill!;
            Assert.Equal(7, apBill.VendorId);
            Assert.Equal(new DateTime(2026, 11, 3), apBill.Date);
            Assert.Equal(new DateTime(2026, 12, 3), apBill.DueDate);
        }

        [Fact]
        public async Task PurchaseVehicle_NoDueDate_ApBillDueDateNull()
        {
            var purchase = Purchase("Credit", 0m, method: null, bankAccountId: null);
            purchase.Due_Date = null;

            await _service.PurchaseVehicle(purchase);

            Assert.Null(_repository.PurchaseCalls[0].ApBill!.DueDate);
        }

        [Fact]
        public async Task PurchaseVehicle_NoPurchaseDate_ApBillDateIsToday()
        {
            var purchase = Purchase("Credit", 0m, method: null, bankAccountId: null);
            purchase.Purchase_Date = null;

            await _service.PurchaseVehicle(purchase);

            Assert.Equal(DateTime.Today, _repository.PurchaseCalls[0].ApBill!.Date);
        }

        [Fact]
        public async Task PurchaseVehicle_PassesPurchaseToRepositoryOnce()
        {
            var purchase = Purchase("Partial", 15000m);

            await _service.PurchaseVehicle(purchase);

            var call = Assert.Single(_repository.PurchaseCalls);
            Assert.Same(purchase, call.Purchase);
        }

        [Fact]
        public async Task PurchaseVehicle_PaymentOrCondition_NeverChangesVehicleCost()
        {
            var partial = Purchase("Partial", 15000m);
            var credit = Purchase("Credit", 0m, method: null, bankAccountId: null);

            var partialResult = await _service.PurchaseVehicle(partial);
            var creditResult = await _service.PurchaseVehicle(credit);

            Assert.Equal(40000m, partialResult.Total_Cost);
            Assert.Equal(40000m, creditResult.Total_Cost);
            Assert.Equal(38000m, partial.Purchase_Price);
            Assert.Equal(2000m, partial.Additional_Costs);
        }

        [Theory]
        [InlineData("paidinfull", "PaidInFull", 40000)]
        [InlineData("PARTIAL", "Partial", 15000)]
        [InlineData(" credit ", "Credit", 0)]
        public async Task PurchaseVehicle_ConditionIsCaseInsensitive_AndNormalized(string given, string expected, int paid)
        {
            var purchase = Purchase(given, paid);

            await _service.PurchaseVehicle(purchase);

            Assert.Equal(expected, _repository.PurchaseCalls[0].Purchase.Payment_Condition);
        }

        [Fact]
        public async Task PurchaseVehicle_RepositoryThrows_ExceptionPropagates()
        {
            _repository.PurchaseException = new InvalidOperationException("db failed");

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => _service.PurchaseVehicle(Purchase("Partial", 15000m)));
        }

        // ---- ValidateAmountPaid ----

        [Fact]
        public async Task PurchaseVehicle_NegativeAmountPaid_Throws_NothingSaved()
        {
            var ex = await Assert.ThrowsAsync<ArgumentException>(
                () => _service.PurchaseVehicle(Purchase("Partial", -1m)));

            Assert.Contains("negative", ex.Message);
            AssertNothingSaved();
        }

        [Fact]
        public async Task PurchaseVehicle_AmountPaidAboveTotalCost_Throws_NothingSaved()
        {
            var ex = await Assert.ThrowsAsync<ArgumentException>(
                () => _service.PurchaseVehicle(Purchase("PaidInFull", 40000.01m)));

            Assert.Contains("exceed", ex.Message);
            AssertNothingSaved();
        }

        [Fact]
        public async Task PurchaseVehicle_AmountPaidEqualsTotalCost_IsAccepted()
        {
            await _service.PurchaseVehicle(Purchase("PaidInFull", 40000m));

            Assert.Single(_repository.PurchaseCalls);
        }

        [Fact]
        public async Task PurchaseVehicle_AmountPaidWithoutBankAccount_Throws_NothingSaved()
        {
            var ex = await Assert.ThrowsAsync<ArgumentException>(
                () => _service.PurchaseVehicle(Purchase("Partial", 15000m, bankAccountId: null)));

            Assert.Contains("bank account", ex.Message);
            AssertNothingSaved();
        }

        [Fact]
        public async Task PurchaseVehicle_AmountPaidWithoutMethod_Throws_NothingSaved()
        {
            var ex = await Assert.ThrowsAsync<ArgumentException>(
                () => _service.PurchaseVehicle(Purchase("Partial", 15000m, method: null)));

            Assert.Contains("method", ex.Message);
            AssertNothingSaved();
        }

        [Fact]
        public async Task PurchaseVehicle_UnknownPaymentMethod_Throws_NothingSaved()
        {
            await Assert.ThrowsAsync<ArgumentException>(
                () => _service.PurchaseVehicle(Purchase("Partial", 15000m, method: "Bitcoin")));

            AssertNothingSaved();
        }

        [Fact]
        public async Task PurchaseVehicle_NumericPaymentMethod_Throws()
        {
            // Enum.TryParse accepts "99"; the service must not.
            await Assert.ThrowsAsync<ArgumentException>(
                () => _service.PurchaseVehicle(Purchase("Partial", 15000m, method: "99")));

            AssertNothingSaved();
        }

        [Theory]
        [InlineData("Cash")]
        [InlineData("Check")]
        [InlineData("Card")]
        [InlineData("Transfer")]
        [InlineData("check")]
        public async Task PurchaseVehicle_EachPaymentMethod_IsAccepted(string method)
        {
            await _service.PurchaseVehicle(Purchase("Partial", 15000m, method: method));

            Assert.Single(_repository.PurchaseCalls);
        }

        [Fact]
        public async Task PurchaseVehicle_ZeroPaid_BankAccountAndMethodNotRequired()
        {
            await _service.PurchaseVehicle(Purchase("Credit", 0m, method: null, bankAccountId: null));

            Assert.Single(_repository.PurchaseCalls);
        }

        // ---- ApplyPaymentCondition ----

        [Theory]
        [InlineData(39999.99)]
        [InlineData(0)]
        public async Task PurchaseVehicle_PaidInFull_AmountNotEqualToTotal_Throws(double paid)
        {
            await Assert.ThrowsAsync<ArgumentException>(
                () => _service.PurchaseVehicle(Purchase("PaidInFull", (decimal)paid)));

            AssertNothingSaved();
        }

        [Theory]
        [InlineData(0)]
        [InlineData(40000)]
        public async Task PurchaseVehicle_Partial_AmountAtOrOutsideBounds_Throws(int paid)
        {
            await Assert.ThrowsAsync<ArgumentException>(
                () => _service.PurchaseVehicle(Purchase("Partial", paid)));

            AssertNothingSaved();
        }

        [Theory]
        [InlineData(0.01)]
        [InlineData(39999.99)]
        public async Task PurchaseVehicle_Partial_AmountJustInsideBounds_IsAccepted(double paid)
        {
            await _service.PurchaseVehicle(Purchase("Partial", (decimal)paid));

            Assert.NotNull(_repository.PurchaseCalls[0].ApBill);
        }

        [Fact]
        public async Task PurchaseVehicle_Credit_WithAmountPaid_Throws()
        {
            await Assert.ThrowsAsync<ArgumentException>(
                () => _service.PurchaseVehicle(Purchase("Credit", 1m)));

            AssertNothingSaved();
        }

        [Theory]
        [InlineData("")]
        [InlineData("Layaway")]
        public async Task PurchaseVehicle_UnknownCondition_Throws(string condition)
        {
            await Assert.ThrowsAsync<ArgumentException>(
                () => _service.PurchaseVehicle(Purchase(condition, 0m)));

            AssertNothingSaved();
        }

        // ---- DeterminePaymentStatus (through AP Bill and response) ----

        [Theory]
        [InlineData(1, "partial")]
        [InlineData(39999, "partial")]
        public async Task PurchaseVehicle_PaidBetweenZeroAndTotal_StatusPartial(int paid, string expected)
        {
            var result = await _service.PurchaseVehicle(Purchase("Partial", paid));

            Assert.Equal(expected, result.Payment_Status);
            Assert.Equal(expected, _repository.PurchaseCalls[0].ApBill!.Status);
        }

        // ---- CreateApBillIfBalance: balance arithmetic ----

        [Fact]
        public async Task PurchaseVehicle_BalanceDueIsTotalCostMinusAmountPaid()
        {
            var result = await _service.PurchaseVehicle(Purchase("Partial", 12345.67m));

            Assert.Equal(27654.33m, result.Balance_Due);
            Assert.Equal(27654.33m, _repository.PurchaseCalls[0].ApBill!.Balance);
        }

        // ---- Story 4 rules still apply ----

        [Fact]
        public async Task PurchaseVehicle_PurchasePriceZero_Throws()
        {
            await Assert.ThrowsAsync<ArgumentException>(
                () => _service.PurchaseVehicle(Purchase("Credit", 0m, price: 0m, method: null, bankAccountId: null)));

            AssertNothingSaved();
        }

        [Fact]
        public async Task PurchaseVehicle_NegativeAdditionalCosts_Throws()
        {
            await Assert.ThrowsAsync<ArgumentException>(
                () => _service.PurchaseVehicle(Purchase("Credit", 0m, additional: -1m, method: null, bankAccountId: null)));

            AssertNothingSaved();
        }

        [Fact]
        public async Task PurchaseVehicle_VinNotSeventeenCharacters_Throws()
        {
            var purchase = Purchase("Credit", 0m, method: null, bankAccountId: null);
            purchase.Vin = "SHORT";

            await Assert.ThrowsAsync<ArgumentException>(() => _service.PurchaseVehicle(purchase));

            AssertNothingSaved();
        }

        [Fact]
        public async Task PurchaseVehicle_VinIsTrimmedBeforeCheckAndSave()
        {
            var purchase = Purchase("Credit", 0m, method: null, bankAccountId: null);
            purchase.Vin = "  1HGCM82633A004352  ";

            await _service.PurchaseVehicle(purchase);

            Assert.Equal("1HGCM82633A004352", Assert.Single(_repository.VinChecks));
            Assert.Equal("1HGCM82633A004352", _repository.PurchaseCalls[0].Purchase.Vin);
        }

        [Fact]
        public async Task PurchaseVehicle_DuplicateVin_Throws()
        {
            _repository.VinTaken = true;

            var ex = await Assert.ThrowsAsync<ArgumentException>(
                () => _service.PurchaseVehicle(Purchase("Partial", 15000m)));

            Assert.Contains("VIN", ex.Message);
            AssertNothingSaved();
        }

        [Fact]
        public async Task PurchaseVehicle_InactiveVendor_Throws()
        {
            _repository.VendorStatus = "inactive";

            await Assert.ThrowsAsync<ArgumentException>(
                () => _service.PurchaseVehicle(Purchase("Partial", 15000m)));

            AssertNothingSaved();
        }

        [Fact]
        public async Task PurchaseVehicle_UnknownVendor_Throws()
        {
            _repository.VendorStatus = null;

            await Assert.ThrowsAsync<ArgumentException>(
                () => _service.PurchaseVehicle(Purchase("Partial", 15000m)));

            AssertNothingSaved();
        }
    }
}
