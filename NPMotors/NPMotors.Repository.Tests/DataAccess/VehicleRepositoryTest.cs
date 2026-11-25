using System.Data;
using System.ComponentModel.DataAnnotations;
using NPMotors.Repository.Models;
using NPMotors.Services.Service;
using Dapper;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.RequestFeatures;
using NPMotors.Repository.Models.Enums;
using NPMotors.Repository.Repositories;

namespace NPMotors.Repository.Tests.DataAccess
{
    public class VehicleRepositoryTest : TestBase, IAsyncLifetime
    {
        private readonly VehicleRepository _repository;
        private readonly VendorRepository _vendorRepository;
        private readonly string _marker = $"itest-{Guid.NewGuid():N}".Substring(0, 12);
        private readonly List<long> _vehicleIds = new();
        private readonly List<long> _vendorIds = new();

        public VehicleRepositoryTest()
        {
            var accounts = new BankAccountRepository(_repositoryContext);
            var posting = new TransactionPostingService(
                new TransactionRepository(_repositoryContext), accounts,
                new BankStatementLineRepository(_repositoryContext), _repositoryContext);
            _repository = new VehicleRepository(_repositoryContext, posting);
            _vendorRepository = new VendorRepository(_repositoryContext);
        }

        private long _purchaseVendorId;
        private long _bankId;
        private long _accountId;

        public async Task InitializeAsync()
        {
            // Vendor, bank and bank account used by the PurchaseVehicle tests (Story 19).
            var vendor = await _vendorRepository.AddVendor(new VendorCreateDto
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
            using (var connection = _repositoryContext.GetDatabaseConnection())
            {
                var p = new { Marker = _marker + "%", BankId = _bankId };
                await connection.ExecuteAsync(
                    "DELETE FROM bank_payment WHERE vehicle_id IN (SELECT id FROM vehicle WHERE vin ILIKE @Marker)", p);
                await connection.ExecuteAsync(
                    "DELETE FROM ap_bill WHERE vehicle_id IN (SELECT id FROM vehicle WHERE vin ILIKE @Marker)", p);
                await connection.ExecuteAsync(
                    "DELETE FROM bank_statement_line WHERE bank_account_id IN (SELECT id FROM bank_account WHERE bank_id = @BankId)", p);
                await connection.ExecuteAsync(
                    @"DELETE FROM ""transaction"" WHERE bank_account_id IN (SELECT id FROM bank_account WHERE bank_id = @BankId)", p);
                await connection.ExecuteAsync("DELETE FROM bank_account WHERE bank_id = @BankId", p);
                await connection.ExecuteAsync("DELETE FROM bank WHERE id = @BankId", p);
            }

            foreach (var id in _vehicleIds)
                await _repository.DeleteVehicle(id);
            using (var connection = _repositoryContext.GetDatabaseConnection())
                await connection.ExecuteAsync("DELETE FROM vehicle WHERE vin ILIKE @Marker", new { Marker = _marker + "%" });
            await _vendorRepository.DeleteVendor(_purchaseVendorId);
            foreach (var id in _vendorIds)
                await _vendorRepository.DeleteVendor(id);
        }

        private async Task<VendorResponseDto> CreateVendorAsync(Status status = Status.Active)
        {
            var vendor = await _vendorRepository.AddVendor(new VendorCreateDto
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
            Make = "Toyota",
            Model = "Camry",
            Year = 2022,
            Color = "Blue",
            Purchase_Date = new DateTime(2026, 1, 15),
            Warehouse = "Main",
            Purchase_Price = 20000m,
            Additional_Costs = 500.50m,
            Selling_Price = 25000m
        };

        [Fact]
        public async Task AddVehicle_CalculatesTotalCost_AndSetsAvailableStatus()
        {
            var vendor = await CreateVendorAsync();

            var created = await _repository.AddVehicle(NewDto(vendor.Id));
            _vehicleIds.Add(created.Id);

            Assert.True(created.Id > 0);
            Assert.Equal(vendor.Id, created.Vendor_Id);
            Assert.Equal(20500.50m, created.Total_Cost);
            Assert.Equal(VehicleStatus.Available, created.Status);
            Assert.Equal(new DateTime(2026, 1, 15), created.Purchase_Date);
        }

        [Fact]
        public async Task AddVehicle_DefaultsAdditionalCostsAndSellingPriceToZero()
        {
            var vendor = await CreateVendorAsync();
            var dto = NewDto(vendor.Id);
            dto.Additional_Costs = null;
            dto.Selling_Price = null;

            var created = await _repository.AddVehicle(dto);
            _vehicleIds.Add(created.Id);

            Assert.Equal(0m, created.Additional_Costs);
            Assert.Equal(0m, created.Selling_Price);
            Assert.Equal(created.Purchase_Price, created.Total_Cost);
        }

        private async Task<VehicleResponseDto> CreateVehicleAsync(
            long vendorId, string suffix, string make, string model,
            string status = "available", decimal purchase = 20000m, decimal additional = 500m, decimal selling = 25000m)
        {
            var dto = NewDto(vendorId, suffix);
            dto.Make = make;
            dto.Model = model;
            dto.Purchase_Price = purchase;
            dto.Additional_Costs = additional;
            dto.Selling_Price = selling;

            var created = await _repository.AddVehicle(dto);
            _vehicleIds.Add(created.Id);

            if (status != "available")
            {
                using IDbConnection connection = _repositoryContext.GetDatabaseConnection();
                await connection.ExecuteAsync("UPDATE vehicle SET status = @Status WHERE id = @Id",
                    new { Status = status, Id = created.Id });
            }
            return created;
        }

        // The marker is part of every test VIN, so searching for it isolates this test's rows.
        private VehicleParameters Scoped(string? status = null, string? search = null) => new()
        {
            Status = status,
            SearchTerm = search ?? _marker
        };

        [Fact]
        public async Task GetAllVehicles_NoStatus_ReturnsAllStatuses()
        {
            var vendor = await CreateVendorAsync();
            var available = await CreateVehicleAsync(vendor.Id, "1", "Toyota", "Camry");
            var reserved = await CreateVehicleAsync(vendor.Id, "2", "Toyota", "Corolla", "reserved");

            var result = await _repository.GetAllVehicles(Scoped());

            Assert.Equal(2, result.MetaData.TotalCount);
            Assert.Contains(result, v => v.Id == available.Id);
            Assert.Contains(result, v => v.Id == reserved.Id);
        }

        [Theory]
        [InlineData("available")]
        [InlineData("reserved")]
        public async Task GetAllVehicles_StatusFilter_ReturnsOnlyThatStatus(string status)
        {
            var vendor = await CreateVendorAsync();
            var available = await CreateVehicleAsync(vendor.Id, "1", "Toyota", "Camry");
            var reserved = await CreateVehicleAsync(vendor.Id, "2", "Toyota", "Corolla", "reserved");

            var result = await _repository.GetAllVehicles(Scoped(status));

            var expected = status == "available" ? available : reserved;
            var only = Assert.Single(result);
            Assert.Equal(expected.Id, only.Id);
            Assert.Equal(1, result.MetaData.TotalCount);
        }

        [Fact]
        public async Task GetAllVehicles_SearchTerm_MatchesVinPartialAndCaseInsensitive()
        {
            var vendor = await CreateVendorAsync();
            var vehicle = await CreateVehicleAsync(vendor.Id, "1", "Toyota", "Camry");
            var mixedCase = "ItEsT-" + _marker.Substring(6);

            var result = await _repository.GetAllVehicles(Scoped(search: mixedCase));

            Assert.Contains(result, v => v.Id == vehicle.Id);
        }

        [Fact]
        public async Task GetAllVehicles_SearchTerm_MatchesMake()
        {
            var vendor = await CreateVendorAsync();
            var token = Guid.NewGuid().ToString("N").Substring(0, 8);
            var match = await CreateVehicleAsync(vendor.Id, "1", $"Mk{token}", "Camry");
            await CreateVehicleAsync(vendor.Id, "2", "Other", "Camry");

            var result = await _repository.GetAllVehicles(Scoped(search: token.Substring(1, 5).ToUpperInvariant()));

            var only = Assert.Single(result);
            Assert.Equal(match.Id, only.Id);
        }

        [Fact]
        public async Task GetAllVehicles_SearchTerm_MatchesModel()
        {
            var vendor = await CreateVendorAsync();
            var token = Guid.NewGuid().ToString("N").Substring(0, 8);
            var match = await CreateVehicleAsync(vendor.Id, "1", "Toyota", $"Md{token}");
            await CreateVehicleAsync(vendor.Id, "2", "Toyota", "Other");

            var result = await _repository.GetAllVehicles(Scoped(search: token.Substring(2, 5).ToUpperInvariant()));

            var only = Assert.Single(result);
            Assert.Equal(match.Id, only.Id);
        }

        [Fact]
        public async Task GetAllVehicles_StatusAndSearch_BothApply()
        {
            var vendor = await CreateVendorAsync();
            var token = Guid.NewGuid().ToString("N").Substring(0, 8);
            var hit = await CreateVehicleAsync(vendor.Id, "1", $"Mk{token}", "Camry", "reserved");
            await CreateVehicleAsync(vendor.Id, "2", $"Mk{token}", "Corolla");          // matches search, wrong status
            await CreateVehicleAsync(vendor.Id, "3", "Other", "Camry", "reserved");     // matches status, wrong search

            var result = await _repository.GetAllVehicles(Scoped("reserved", $"mk{token}"));

            var only = Assert.Single(result);
            Assert.Equal(hit.Id, only.Id);
            Assert.Equal(1, result.MetaData.TotalCount);
        }

        [Fact]
        public async Task GetAllVehicles_OrderedByMakeThenModel()
        {
            var vendor = await CreateVendorAsync();
            var token = Guid.NewGuid().ToString("N").Substring(0, 8).ToUpperInvariant();
            var aZ = await CreateVehicleAsync(vendor.Id, "1", $"{token}A", "Z");
            var bA = await CreateVehicleAsync(vendor.Id, "2", $"{token}B", "A");
            var aC = await CreateVehicleAsync(vendor.Id, "3", $"{token}A", "C");

            var result = await _repository.GetAllVehicles(Scoped());

            Assert.Equal(new[] { aC.Id, aZ.Id, bA.Id }, result.Select(v => v.Id).ToArray());
        }

        [Fact]
        public async Task GetAllVehicles_Paging_SecondPageAndTotalCountOfAllMatches()
        {
            var vendor = await CreateVendorAsync();
            var token = Guid.NewGuid().ToString("N").Substring(0, 8).ToUpperInvariant();
            var first = await CreateVehicleAsync(vendor.Id, "1", $"{token}A", "A");
            var second = await CreateVehicleAsync(vendor.Id, "2", $"{token}B", "A");
            var third = await CreateVehicleAsync(vendor.Id, "3", $"{token}C", "A");

            var page2 = await _repository.GetAllVehicles(
                new VehicleParameters { SearchTerm = _marker, PageNumber = 2, PageSize = 1 });

            var only = Assert.Single(page2);
            Assert.Equal(second.Id, only.Id);
            Assert.Equal(3, page2.MetaData.TotalCount);
            Assert.Equal(3, page2.MetaData.TotalPages);
            Assert.Equal(2, page2.MetaData.CurrentPage);
            Assert.Equal(1, page2.MetaData.PageSize);
        }

        [Fact]
        public async Task GetAllVehicles_NoMatches_ReturnsEmptyPagedList()
        {
            var result = await _repository.GetAllVehicles(
                new VehicleParameters { SearchTerm = $"no-such-vehicle-{Guid.NewGuid():N}" });

            Assert.NotNull(result);
            Assert.Empty(result);
            Assert.Equal(0, result.MetaData.TotalCount);
        }

        [Fact]
        public async Task GetAllVehicles_IncludesVendorName()
        {
            var vendor = await CreateVendorAsync();
            await CreateVehicleAsync(vendor.Id, "1", "Toyota", "Camry");

            var result = await _repository.GetAllVehicles(Scoped());

            var only = Assert.Single(result);
            Assert.Equal(vendor.Name, only.Vendor_Name);
            Assert.Equal(vendor.Id, only.Vendor_Id);
        }

        [Fact]
        public async Task GetAllVehicles_CalculatedFieldsAreLeftAtDefaults()
        {
            var vendor = await CreateVendorAsync();
            await CreateVehicleAsync(vendor.Id, "1", "Toyota", "Camry");

            var only = Assert.Single(await _repository.GetAllVehicles(Scoped()));

            Assert.Equal(20000m, only.Purchase_Price);
            Assert.Equal(500m, only.Additional_Costs);
            Assert.Equal(25000m, only.Selling_Price);
            Assert.Equal(0m, only.Total_Cost);
            Assert.Equal(0m, only.Gross_Profit);
            Assert.Null(only.Gross_Margin);
            Assert.False(only.Is_Available);
        }

        [Fact]
        public async Task GetVehicleById_Existing_ReturnsVehicleWithVendorName()
        {
            var vendor = await CreateVendorAsync();
            var created = await CreateVehicleAsync(vendor.Id, "1", "Toyota", "Camry", "reserved");

            var result = await _repository.GetVehicleById(created.Id);

            Assert.NotNull(result);
            Assert.Equal(created.Id, result!.Id);
            Assert.Equal(created.Vin, result.Vin);
            Assert.Equal(vendor.Name, result.Vendor_Name);
            Assert.Equal(VehicleStatus.Reserved, result.Status);
            Assert.Equal(0m, result.Gross_Profit);
        }

        [Fact]
        public async Task GetVehicleById_Unknown_ReturnsNull()
        {
            Assert.Null(await _repository.GetVehicleById(-1));
        }

        [Fact]
        public async Task GetVendorStatus_ReturnsLowerCaseStatus_OrNullWhenMissing()
        {
            var inactive = await CreateVendorAsync(Status.Inactive);

            Assert.Equal("inactive", await _repository.GetVendorStatus(inactive.Id));
            Assert.Null(await _repository.GetVendorStatus(-1));
        }

        // ---- Story 19: PurchaseVehicle ----

        private class PaymentRow
        {
            public long Id { get; set; }
            public long Transaction_Id { get; set; }
            public long Bank_Account_Id { get; set; }
            public long? Vendor_Id { get; set; }
            public long? Ap_Bill_Id { get; set; }
            public long? Vehicle_Id { get; set; }
            public string Payment_Number { get; set; } = "";
            public DateTime Payment_Date { get; set; }
            public string Payment_Method { get; set; } = "";
            public string? Reference { get; set; }
            public decimal Amount { get; set; }
            public string Status { get; set; } = "";
        }

        private class ApBillRow
        {
            public long Id { get; set; }
            public string Number { get; set; } = "";
            public long Vendor_Id { get; set; }
            public long? Vehicle_Id { get; set; }
            public DateTime Date { get; set; }
            public DateTime? Due_Date { get; set; }
            public decimal Amount { get; set; }
            public decimal Paid { get; set; }
            public decimal Balance { get; set; }
            public string Status { get; set; } = "";
        }

        private class TransactionRow
        {
            public long Id { get; set; }
            public string Type { get; set; } = "";
            public string Direction { get; set; } = "";
            public decimal Amount { get; set; }
            public string Status { get; set; } = "";
            public string Source_Number { get; set; } = "";
            public string? Party { get; set; }
        }

        private class StatementRow
        {
            public long Transaction_Id { get; set; }
            public decimal Withdrawal { get; set; }
            public decimal Deposit { get; set; }
            public decimal Running_Balance { get; set; }
        }

        private async Task<long> InsertAccountAsync(decimal balance, string status = "active")
        {
            using var connection = _repositoryContext.GetDatabaseConnection();
            return await connection.ExecuteScalarAsync<long>(
                @"INSERT INTO bank_account (bank_id, name, type, opening_balance, book_balance, status)
                  VALUES (@BankId, @Name, 'Checking', @Balance, @Balance, @Status) RETURNING id",
                new { BankId = _bankId, Name = $"{_marker} Account {Guid.NewGuid():N}".Substring(0, 40), Balance = balance, Status = status });
        }

        // Total cost 40 000 (38 000 + 2 000).
        private VehiclePurchaseCreateDto Purchase(string condition, decimal paid, string suffix = "1",
            string? method = "check", long? bankAccountId = null) => new()
        {
            Vendor_Id = _purchaseVendorId,
            Vin = $"{_marker}{suffix}".ToUpperInvariant().PadRight(17, '0').Substring(0, 17),
            Make = "Honda",
            Model = "Accord",
            Year = 2022,
            Purchase_Date = new DateTime(2026, 11, 3),
            Purchase_Price = 38000m,
            Additional_Costs = 2000m,
            Selling_Price = 45000m,
            Payment_Condition = condition,
            Amount_Paid = paid,
            Payment_Method = paid > 0 ? method : null,
            Reference = paid > 0 ? "CHK-1001" : null,
            Bank_Account_Id = paid > 0 ? bankAccountId ?? _accountId : null,
            Due_Date = new DateTime(2026, 12, 3)
        };

        private static APBill ApBillFor(VehiclePurchaseCreateDto p) => new()
        {
            VendorId = p.Vendor_Id,
            Amount = 40000m,
            Paid = p.Amount_Paid,
            Balance = 40000m - p.Amount_Paid,
            Status = p.Amount_Paid > 0 ? "partial" : "unpaid",
            Date = p.Purchase_Date!.Value,
            DueDate = p.Due_Date
        };

        private async Task<decimal> BalanceAsync(long? accountId = null)
        {
            using var connection = _repositoryContext.GetDatabaseConnection();
            return await connection.ExecuteScalarAsync<decimal>(
                "SELECT book_balance FROM bank_account WHERE id = @Id", new { Id = accountId ?? _accountId });
        }

        private async Task<int> CountAsync(string sql, object? param = null)
        {
            using var connection = _repositoryContext.GetDatabaseConnection();
            return await connection.ExecuteScalarAsync<int>(sql, param);
        }

        private Task<int> VehicleCountAsync() =>
            CountAsync("SELECT COUNT(*) FROM vehicle WHERE vin ILIKE @Marker", new { Marker = _marker + "%" });

        private async Task<T?> RowAsync<T>(string sql, object param)
        {
            using var connection = _repositoryContext.GetDatabaseConnection();
            return await connection.QuerySingleOrDefaultAsync<T>(sql, param);
        }

        private Task<PaymentRow?> PaymentForVehicleAsync(long vehicleId) => RowAsync<PaymentRow>(
            @"SELECT id, transaction_id, bank_account_id, vendor_id, ap_bill_id, vehicle_id, payment_number, payment_date,
                     payment_method, reference, amount, status
              FROM bank_payment WHERE vehicle_id = @Id", new { Id = vehicleId });

        private Task<ApBillRow?> ApBillForVehicleAsync(long vehicleId) => RowAsync<ApBillRow>(
            @"SELECT id, number, vendor_id, vehicle_id, date, due_date, amount, paid, balance, status
              FROM ap_bill WHERE vehicle_id = @Id", new { Id = vehicleId });

        // ---- Story example: partial ----

        [Fact]
        public async Task PurchaseVehicle_Partial_SavesVehicleApBillPaymentTransactionStatementAndBalance()
        {
            var purchase = Purchase("Partial", 15000m);

            var result = await _repository.PurchaseVehicle(purchase, ApBillFor(purchase));

            Assert.Equal(40000m, result.Total_Cost);
            Assert.Equal(VehicleStatus.Available, result.Status);
            Assert.Equal("Partial", result.Payment_Condition);
            Assert.Equal(15000m, result.Amount_Paid);
            Assert.Equal($"{_marker} Vendor", result.Vendor_Name);

            var ap = await ApBillForVehicleAsync(result.Id);
            Assert.NotNull(ap);
            Assert.Equal(result.Ap_Bill_Id, ap!.Id);
            Assert.Equal(result.Ap_Bill_Number, ap.Number);
            Assert.Matches($@"^AP-\d{{4}}-{ap.Id:D6}$", ap.Number);
            Assert.Equal(_purchaseVendorId, ap.Vendor_Id);
            Assert.Equal(40000m, ap.Amount);
            Assert.Equal(15000m, ap.Paid);
            Assert.Equal(25000m, ap.Balance);
            Assert.Equal("partial", ap.Status);
            Assert.Equal(new DateTime(2026, 11, 3), ap.Date);
            Assert.Equal(new DateTime(2026, 12, 3), ap.Due_Date);

            var payment = await PaymentForVehicleAsync(result.Id);
            Assert.NotNull(payment);
            Assert.Equal(result.Bank_Payment_Id, payment!.Id);
            Assert.Equal(result.Payment_Number, payment.Payment_Number);
            Assert.Matches($@"^PAY-\d{{4}}-{payment.Id:D6}$", payment.Payment_Number);
            Assert.Equal(ap.Id, payment.Ap_Bill_Id);
            Assert.Equal(result.Id, payment.Vehicle_Id);
            Assert.Equal(_purchaseVendorId, payment.Vendor_Id);
            Assert.Equal(_accountId, payment.Bank_Account_Id);
            Assert.Equal(15000m, payment.Amount);
            Assert.Equal("Check", payment.Payment_Method);
            Assert.Equal("CHK-1001", payment.Reference);
            Assert.Equal("Posted", payment.Status);
            Assert.Equal(new DateTime(2026, 11, 3), payment.Payment_Date);

            var tx = await RowAsync<TransactionRow>(
                @"SELECT id, type, direction::text AS direction, amount, status, source_number, party
                  FROM ""transaction"" WHERE id = @Id", new { Id = payment.Transaction_Id });
            Assert.NotNull(tx);
            Assert.Equal(result.Transaction_Id, tx!.Id);
            Assert.Equal("BankPayment", tx.Type);
            Assert.Equal("out", tx.Direction);
            Assert.Equal(15000m, tx.Amount);
            Assert.Equal("posted", tx.Status);
            Assert.Equal(payment.Payment_Number, tx.Source_Number);
            Assert.Equal($"{_marker} Vendor", tx.Party);

            var line = await RowAsync<StatementRow>(
                @"SELECT transaction_id, withdrawal, deposit, running_balance
                  FROM bank_statement_line WHERE transaction_id = @Id", new { Id = tx.Id });
            Assert.NotNull(line);
            Assert.Equal(15000m, line!.Withdrawal);
            Assert.Equal(0m, line.Deposit);
            Assert.Equal(85000m, line.Running_Balance);

            Assert.Equal(85000m, await BalanceAsync());
            Assert.Equal(85000m, result.Book_Balance);
        }

        [Fact]
        public async Task PurchaseVehicle_PaymentMethodStoredInCanonicalCase()
        {
            var purchase = Purchase("Partial", 100m, method: "tRaNsFeR");

            var result = await _repository.PurchaseVehicle(purchase, ApBillFor(purchase));

            Assert.Equal("Transfer", (await PaymentForVehicleAsync(result.Id))!.Payment_Method);
        }

        // ---- Paid in full ----

        [Fact]
        public async Task PurchaseVehicle_PaidInFull_NoApBill_PaymentLinkedToVehicleOnly()
        {
            var purchase = Purchase("PaidInFull", 40000m);

            var result = await _repository.PurchaseVehicle(purchase, null);

            Assert.Null(result.Ap_Bill_Id);
            Assert.Null(result.Ap_Bill_Number);
            Assert.Null(await ApBillForVehicleAsync(result.Id));

            var payment = await PaymentForVehicleAsync(result.Id);
            Assert.NotNull(payment);
            Assert.Null(payment!.Ap_Bill_Id);
            Assert.Equal(result.Id, payment.Vehicle_Id);
            Assert.Equal(40000m, payment.Amount);
            Assert.Equal(60000m, await BalanceAsync());
        }

        // ---- Credit ----

        [Fact]
        public async Task PurchaseVehicle_Credit_CreatesApBillOnly_NoPaymentTransactionOrBalanceChange()
        {
            var purchase = Purchase("Credit", 0m);

            var result = await _repository.PurchaseVehicle(purchase, ApBillFor(purchase));

            var ap = await ApBillForVehicleAsync(result.Id);
            Assert.NotNull(ap);
            Assert.Equal(40000m, ap!.Amount);
            Assert.Equal(0m, ap.Paid);
            Assert.Equal(40000m, ap.Balance);
            Assert.Equal("unpaid", ap.Status);

            Assert.Null(await PaymentForVehicleAsync(result.Id));
            Assert.Null(result.Bank_Payment_Id);
            Assert.Null(result.Payment_Number);
            Assert.Null(result.Transaction_Id);
            Assert.Null(result.Book_Balance);
            Assert.Equal(0, await CountAsync(
                @"SELECT COUNT(*) FROM ""transaction"" WHERE bank_account_id = @Id", new { Id = _accountId }));
            Assert.Equal(100000m, await BalanceAsync());
        }

        // ---- Vehicle cost ----

        [Fact]
        public async Task PurchaseVehicle_TotalCostStoredFromPrices_AndUnchangedWhenApBillIsPaid()
        {
            var purchase = Purchase("Partial", 15000m);
            var result = await _repository.PurchaseVehicle(purchase, ApBillFor(purchase));

            using (var connection = _repositoryContext.GetDatabaseConnection())
            {
                await connection.ExecuteAsync(
                    "UPDATE ap_bill SET paid = amount, balance = 0, status = 'paid' WHERE id = @Id",
                    new { Id = result.Ap_Bill_Id });
            }

            var totalCost = await RowAsync<decimal>("SELECT total_cost FROM vehicle WHERE id = @Id", new { Id = result.Id });
            Assert.Equal(40000m, totalCost);
        }

        [Fact]
        public async Task PurchaseVehicle_TotalCostSameForPaidInFullPartialAndCredit()
        {
            var full = await _repository.PurchaseVehicle(Purchase("PaidInFull", 40000m, "1"), null);
            var partialDto = Purchase("Partial", 1m, "2");
            var partial = await _repository.PurchaseVehicle(partialDto, ApBillFor(partialDto));
            var creditDto = Purchase("Credit", 0m, "3");
            var credit = await _repository.PurchaseVehicle(creditDto, ApBillFor(creditDto));

            Assert.Equal(40000m, full.Total_Cost);
            Assert.Equal(40000m, partial.Total_Cost);
            Assert.Equal(40000m, credit.Total_Cost);
        }

        // ---- Several purchases ----

        [Fact]
        public async Task PurchaseVehicle_TwoPurchases_RunningBalanceAndBookBalanceFollow()
        {
            var first = Purchase("Partial", 15000m, "1");
            var second = Purchase("Partial", 5000m, "2");

            await _repository.PurchaseVehicle(first, ApBillFor(first));
            var result = await _repository.PurchaseVehicle(second, ApBillFor(second));

            Assert.Equal(80000m, result.Book_Balance);
            var line = await RowAsync<StatementRow>(
                "SELECT transaction_id, withdrawal, deposit, running_balance FROM bank_statement_line WHERE transaction_id = @Id",
                new { Id = result.Transaction_Id });
            Assert.Equal(80000m, line!.Running_Balance);
        }

        // ---- Failures save nothing (AC 6 and 7) ----

        [Fact]
        public async Task PurchaseVehicle_PaymentAboveBookBalance_Throws_AndSavesNothing()
        {
            var poorAccount = await InsertAccountAsync(10000m);
            var purchase = Purchase("Partial", 15000m, bankAccountId: poorAccount);

            await Assert.ThrowsAsync<ValidationException>(
                () => _repository.PurchaseVehicle(purchase, ApBillFor(purchase)));

            await AssertNothingSavedAsync(poorAccount, 10000m);
        }

        [Fact]
        public async Task PurchaseVehicle_PaymentEqualToBookBalance_IsAccepted_BalanceBecomesZero()
        {
            var exactAccount = await InsertAccountAsync(15000m);
            var purchase = Purchase("Partial", 15000m, bankAccountId: exactAccount);

            var result = await _repository.PurchaseVehicle(purchase, ApBillFor(purchase));

            Assert.Equal(0m, result.Book_Balance);
            Assert.Equal(0m, await BalanceAsync(exactAccount));
        }

        [Fact]
        public async Task PurchaseVehicle_InactiveBankAccount_Throws_AndSavesNothing()
        {
            var inactive = await InsertAccountAsync(50000m, "inactive");
            var purchase = Purchase("Partial", 15000m, bankAccountId: inactive);

            await Assert.ThrowsAsync<ValidationException>(
                () => _repository.PurchaseVehicle(purchase, ApBillFor(purchase)));

            await AssertNothingSavedAsync(inactive, 50000m);
        }

        [Fact]
        public async Task PurchaseVehicle_UnknownBankAccount_Throws_AndSavesNothing()
        {
            var purchase = Purchase("Partial", 15000m, bankAccountId: long.MaxValue);

            await Assert.ThrowsAsync<ValidationException>(
                () => _repository.PurchaseVehicle(purchase, ApBillFor(purchase)));

            Assert.Equal(0, await VehicleCountAsync());
            Assert.Equal(100000m, await BalanceAsync());
        }

        [Fact]
        public async Task PurchaseVehicle_DuplicateVin_Throws_AndSavesNothingMore()
        {
            var first = Purchase("Partial", 15000m);
            await _repository.PurchaseVehicle(first, ApBillFor(first));
            var second = Purchase("Partial", 5000m);

            var ex = await Assert.ThrowsAsync<ArgumentException>(
                () => _repository.PurchaseVehicle(second, ApBillFor(second)));

            Assert.Contains("VIN", ex.Message);
            Assert.Equal(1, await VehicleCountAsync());
            Assert.Equal(85000m, await BalanceAsync());
            Assert.Equal(1, await CountAsync(
                @"SELECT COUNT(*) FROM ""transaction"" WHERE bank_account_id = @Id", new { Id = _accountId }));
        }

        [Fact]
        public async Task PurchaseVehicle_SecondApBillForSameVehicle_NotPossible_UniqueVehicle()
        {
            var purchase = Purchase("Partial", 15000m);
            var result = await _repository.PurchaseVehicle(purchase, ApBillFor(purchase));

            using var connection = _repositoryContext.GetDatabaseConnection();
            await Assert.ThrowsAnyAsync<Exception>(() => connection.ExecuteAsync(
                @"INSERT INTO ap_bill (vendor_id, vehicle_id, amount, created_by) VALUES (@V, @Id, 1, 'itest')",
                new { V = _purchaseVendorId, Id = result.Id }));
        }

        private async Task AssertNothingSavedAsync(long accountId, decimal expectedBalance)
        {
            Assert.Equal(0, await VehicleCountAsync());
            Assert.Equal(0, await CountAsync(
                "SELECT COUNT(*) FROM ap_bill a JOIN vehicle v ON v.id = a.vehicle_id WHERE v.vin ILIKE @Marker",
                new { Marker = _marker + "%" }));
            Assert.Equal(0, await CountAsync(
                "SELECT COUNT(*) FROM bank_payment WHERE bank_account_id = @Id", new { Id = accountId }));
            Assert.Equal(0, await CountAsync(
                @"SELECT COUNT(*) FROM ""transaction"" WHERE bank_account_id = @Id", new { Id = accountId }));
            Assert.Equal(0, await CountAsync(
                "SELECT COUNT(*) FROM bank_statement_line WHERE bank_account_id = @Id", new { Id = accountId }));
            Assert.Equal(expectedBalance, await BalanceAsync(accountId));
        }
    }
}
