using System.Data;
using Dapper;
using NPMotors.Repository.Models;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.Models.Enums;
using NPMotors.Repository.Repositories;
using NPMotors.Repository.RequestFeatures;

namespace NPMotors.Repository.Tests.DataAccess
{
    // Story 20: APBillRepository (GetApBillById, UpdateApBillPayment, GetOpenBalanceByVendor) and
    // VendorRepository.UpdateVendorBalance. The bills are committed in InitializeAsync (so reads on a separate
    // connection see them) and removed by the marker sweep; every write runs in a transaction that is never committed.
    public class APBillRepositoryTest : TestBase, IAsyncLifetime
    {
        private readonly APBillRepository _repository;
        private readonly VendorRepository _vendorRepository;
        private readonly string _marker = $"itest-{Guid.NewGuid():N}".Substring(0, 12);
        private long _vendorId;
        private long _otherVendorId;
        private long _emptyVendorId;
        private long _vehicleId;
        private long _openBillId;      // amount 4000, paid 1500, balance 2500, partial, linked to the vehicle
        private long _paidBillId;      // amount 1000, paid 1000, balance 0, paid
        private long _otherVendorBillId; // amount 700, unpaid

        public APBillRepositoryTest()
        {
            _repository = new APBillRepository(_repositoryContext);
            _vendorRepository = new VendorRepository(_repositoryContext);
        }

        private class BillRow
        {
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

        public async Task InitializeAsync()
        {
            _vendorId = await AddVendorAsync("Vendor");
            _otherVendorId = await AddVendorAsync("Other");
            _emptyVendorId = await AddVendorAsync("Empty");

            var vehicle = await new VehicleRepository(_repositoryContext, null!).AddVehicle(new VehicleCreateDto
            {
                Vendor_Id = _vendorId,
                Vin = $"{_marker}1".ToUpperInvariant().PadRight(17, '0').Substring(0, 17),
                Make = "Honda", Model = "Civic", Purchase_Price = 4000m
            });
            _vehicleId = vehicle.Id;

            _openBillId = await InsertBillAsync(_vendorId, 4000m, 1500m, "partial", _vehicleId);
            _paidBillId = await InsertBillAsync(_vendorId, 1000m, 1000m, "paid", null);
            _otherVendorBillId = await InsertBillAsync(_otherVendorId, 700m, 0m, "unpaid", null);
        }

        public async Task DisposeAsync()
        {
            using IDbConnection connection = _repositoryContext.GetDatabaseConnection();
            var pattern = new { Pattern = _marker + "%", VinPattern = _marker.ToUpperInvariant() + "%" };
            await connection.ExecuteAsync(
                "DELETE FROM ap_bill WHERE vendor_id IN (SELECT id FROM vendor WHERE name LIKE @Pattern)", pattern);
            await connection.ExecuteAsync("DELETE FROM vehicle WHERE vin LIKE @VinPattern", pattern);
            await connection.ExecuteAsync("DELETE FROM vendor WHERE name LIKE @Pattern", pattern);
        }

        private async Task<long> AddVendorAsync(string name) =>
            (await _vendorRepository.AddVendor(new VendorCreateDto
            {
                Name = $"{_marker} {name}", Type = VendorType.Vehicles, Status = Status.Active
            })).Id;

        private async Task<long> InsertBillAsync(long vendorId, decimal amount, decimal paid, string status, long? vehicleId)
        {
            using IDbConnection connection = _repositoryContext.GetDatabaseConnection();
            return await connection.ExecuteScalarAsync<long>(
                @"INSERT INTO ap_bill (number, vendor_id, vehicle_id, date, due_date, amount, paid, balance, status, created_by)
                  VALUES (@Number, @VendorId, @VehicleId, '2026-10-01', '2026-12-01', @Amount, @Paid, @Balance, @Status, 'itest')
                  RETURNING id",
                new
                {
                    Number = $"AP-{_marker}-{Guid.NewGuid():N}".Substring(0, 30), VendorId = vendorId, VehicleId = vehicleId,
                    Amount = amount, Paid = paid, Balance = amount - paid, Status = status
                });
        }

        private static (IDbConnection Connection, IDbTransaction Tx) Begin()
        {
            var connection = _repositoryContext.GetDatabaseConnection();
            if (connection.State != ConnectionState.Open)
                connection.Open();
            return (connection, connection.BeginTransaction());
        }

        private const string BillSelect =
            "SELECT number, vendor_id, vehicle_id, date, due_date, amount, paid, balance, status FROM ap_bill WHERE id = @Id";

        // ---- GetApBillById ----

        [Fact]
        public async Task GetApBillById_Existing_ReturnsEveryColumn()
        {
            var bill = await _repository.GetApBillById(_openBillId, null);

            Assert.NotNull(bill);
            Assert.Equal(_openBillId, bill!.Id);
            Assert.StartsWith($"AP-{_marker}-", bill.Number);
            Assert.Equal(_vendorId, bill.VendorId);
            Assert.Equal(_vehicleId, bill.VehicleId);
            Assert.Equal(new DateTime(2026, 10, 1), bill.Date);
            Assert.Equal(new DateTime(2026, 12, 1), bill.DueDate);
            Assert.Equal(4000m, bill.Amount);
            Assert.Equal(1500m, bill.Paid);
            Assert.Equal(2500m, bill.Balance);
            Assert.Equal("partial", bill.Status);
        }

        [Fact]
        public async Task GetApBillById_BillWithoutVehicle_ReturnsNullVehicleId()
        {
            var bill = await _repository.GetApBillById(_paidBillId, null);

            Assert.NotNull(bill);
            Assert.Null(bill!.VehicleId);
            Assert.Equal("paid", bill.Status);
        }

        [Fact]
        public async Task GetApBillById_Unknown_ReturnsNull()
        {
            Assert.Null(await _repository.GetApBillById(long.MaxValue, null));
        }

        [Fact]
        public async Task GetApBillById_WithTransaction_ReturnsTheBill()
        {
            var (connection, tx) = Begin();
            using (connection) using (tx)
            {
                var bill = await _repository.GetApBillById(_openBillId, tx);

                Assert.NotNull(bill);
                Assert.Equal(2500m, bill!.Balance);
            }
        }

        [Fact]
        public async Task GetApBillById_WithTransactionUnknown_ReturnsNull()
        {
            var (connection, tx) = Begin();
            using (connection) using (tx)
            {
                Assert.Null(await _repository.GetApBillById(long.MaxValue, tx));
            }
        }

        [Fact]
        public async Task GetApBillById_WithTransaction_LocksTheRowUntilTheTransactionEnds()
        {
            var (connection, tx) = Begin();
            using (connection) using (tx)
            {
                await _repository.GetApBillById(_openBillId, tx);

                // A second connection cannot take the same lock while the first transaction is open.
                using IDbConnection other = _repositoryContext.GetDatabaseConnection();
                await Assert.ThrowsAnyAsync<Exception>(() => other.ExecuteScalarAsync<long>(
                    "SELECT id FROM ap_bill WHERE id = @Id FOR UPDATE NOWAIT", new { Id = _openBillId }));
            }
        }

        [Fact]
        public async Task GetApBillById_WithoutTransaction_DoesNotLockTheRow()
        {
            await _repository.GetApBillById(_openBillId, null);

            using IDbConnection other = _repositoryContext.GetDatabaseConnection();
            var id = await other.ExecuteScalarAsync<long>(
                "SELECT id FROM ap_bill WHERE id = @Id FOR UPDATE NOWAIT", new { Id = _openBillId });
            Assert.Equal(_openBillId, id);
        }

        // ---- UpdateApBillPayment ----

        [Fact]
        public async Task UpdateApBillPayment_ChangesPaidBalanceAndStatus_AndNothingElse()
        {
            var bill = (await _repository.GetApBillById(_openBillId, null))!;
            bill.Paid = 2500m;
            bill.Balance = 1500m;
            bill.Status = "partial";
            bill.Amount = 999999m; // must never be written
            bill.VehicleId = null; // must never be written

            var (connection, tx) = Begin();
            using (connection) using (tx)
            {
                var rows = await _repository.UpdateApBillPayment(bill, tx);

                Assert.Equal(1, rows);
                var row = await connection.QuerySingleAsync<BillRow>(BillSelect, new { Id = _openBillId }, tx);
                Assert.Equal(2500m, row.Paid);
                Assert.Equal(1500m, row.Balance);
                Assert.Equal("partial", row.Status);
                Assert.Equal(4000m, row.Amount);
                Assert.Equal(_vehicleId, row.Vehicle_Id);
                Assert.Equal(_vendorId, row.Vendor_Id);
                Assert.Equal(new DateTime(2026, 12, 1), row.Due_Date);
                Assert.StartsWith($"AP-{_marker}-", row.Number);
            }
        }

        [Fact]
        public async Task UpdateApBillPayment_FullPayment_StoresPaidStatusAndZeroBalance()
        {
            var bill = (await _repository.GetApBillById(_openBillId, null))!;
            bill.Paid = 4000m;
            bill.Balance = 0m;
            bill.Status = "paid";

            var (connection, tx) = Begin();
            using (connection) using (tx)
            {
                await _repository.UpdateApBillPayment(bill, tx);

                var row = await connection.QuerySingleAsync<BillRow>(BillSelect, new { Id = _openBillId }, tx);
                Assert.Equal(4000m, row.Paid);
                Assert.Equal(0m, row.Balance);
                Assert.Equal("paid", row.Status);
            }
        }

        [Fact]
        public async Task UpdateApBillPayment_OnlyTouchesTheGivenBill()
        {
            var bill = (await _repository.GetApBillById(_openBillId, null))!;
            bill.Paid = 4000m;
            bill.Balance = 0m;
            bill.Status = "paid";

            var (connection, tx) = Begin();
            using (connection) using (tx)
            {
                await _repository.UpdateApBillPayment(bill, tx);

                var other = await connection.QuerySingleAsync<BillRow>(BillSelect, new { Id = _otherVendorBillId }, tx);
                Assert.Equal(0m, other.Paid);
                Assert.Equal(700m, other.Balance);
                Assert.Equal("unpaid", other.Status);
            }
        }

        [Fact]
        public async Task UpdateApBillPayment_UnknownBill_ReturnsZero()
        {
            var bill = new APBill { Id = long.MaxValue, Paid = 1m, Balance = 1m, Status = "partial" };
            var (connection, tx) = Begin();
            using (connection) using (tx)
            {
                Assert.Equal(0, await _repository.UpdateApBillPayment(bill, tx));
            }
        }

        [Fact]
        public async Task UpdateApBillPayment_NotCommitted_LeavesTheBillUnchanged()
        {
            var bill = (await _repository.GetApBillById(_openBillId, null))!;
            bill.Paid = 4000m;
            bill.Balance = 0m;
            bill.Status = "paid";

            var (connection, tx) = Begin();
            using (connection)
            {
                using (tx)
                {
                    await _repository.UpdateApBillPayment(bill, tx);
                    tx.Rollback();
                }

                var row = await connection.QuerySingleAsync<BillRow>(BillSelect, new { Id = _openBillId });
                Assert.Equal(1500m, row.Paid);
                Assert.Equal(2500m, row.Balance);
                Assert.Equal("partial", row.Status);
            }
        }

        // ---- GetOpenBalanceByVendor ----

        [Fact]
        public async Task GetOpenBalanceByVendor_SumsOnlyTheVendorsOpenBills()
        {
            var (connection, tx) = Begin();
            using (connection) using (tx)
            {
                // Open bill 2500; the paid bill (balance 0) and the other vendor's 700 are excluded.
                Assert.Equal(2500m, await _repository.GetOpenBalanceByVendor(_vendorId, tx));
                Assert.Equal(700m, await _repository.GetOpenBalanceByVendor(_otherVendorId, tx));
            }
        }

        [Fact]
        public async Task GetOpenBalanceByVendor_VendorWithoutBills_ReturnsZero()
        {
            var (connection, tx) = Begin();
            using (connection) using (tx)
            {
                Assert.Equal(0m, await _repository.GetOpenBalanceByVendor(_emptyVendorId, tx));
            }
        }

        [Fact]
        public async Task GetOpenBalanceByVendor_SeesChangesMadeInTheSameTransaction()
        {
            var (connection, tx) = Begin();
            using (connection) using (tx)
            {
                await connection.ExecuteAsync(
                    "UPDATE ap_bill SET paid = amount, balance = 0, status = 'paid' WHERE id = @Id", new { Id = _openBillId }, tx);

                Assert.Equal(0m, await _repository.GetOpenBalanceByVendor(_vendorId, tx));
            }
        }

        [Fact]
        public async Task GetOpenBalanceByVendor_AddsUpSeveralOpenBills()
        {
            var (connection, tx) = Begin();
            using (connection) using (tx)
            {
                await connection.ExecuteAsync(
                    @"INSERT INTO ap_bill (number, vendor_id, amount, paid, balance, status, created_by)
                      VALUES (@Number, @VendorId, 300, 0, 300, 'unpaid', 'itest')",
                    new { Number = $"AP-{_marker}-x", VendorId = _vendorId }, tx);

                Assert.Equal(2800m, await _repository.GetOpenBalanceByVendor(_vendorId, tx));
            }
        }

        // ---- VendorRepository.UpdateVendorBalance ----

        [Fact]
        public async Task UpdateVendorBalance_SetsTheBalance_AndReturnsOneRow()
        {
            var (connection, tx) = Begin();
            using (connection) using (tx)
            {
                var rows = await _vendorRepository.UpdateVendorBalance(_vendorId, 1234.56m, tx);

                Assert.Equal(1, rows);
                Assert.Equal(1234.56m, await connection.ExecuteScalarAsync<decimal>(
                    "SELECT balance FROM vendor WHERE id = @Id", new { Id = _vendorId }, tx));
            }
        }

        [Fact]
        public async Task UpdateVendorBalance_OnlyTouchesTheGivenVendor()
        {
            var (connection, tx) = Begin();
            using (connection) using (tx)
            {
                await _vendorRepository.UpdateVendorBalance(_vendorId, 99m, tx);

                Assert.Equal(0m, await connection.ExecuteScalarAsync<decimal>(
                    "SELECT balance FROM vendor WHERE id = @Id", new { Id = _otherVendorId }, tx));
            }
        }

        [Fact]
        public async Task UpdateVendorBalance_UnknownVendor_ReturnsZero()
        {
            var (connection, tx) = Begin();
            using (connection) using (tx)
            {
                Assert.Equal(0, await _vendorRepository.UpdateVendorBalance(long.MaxValue, 1m, tx));
            }
        }

        [Fact]
        public async Task UpdateVendorBalance_NotCommitted_LeavesTheBalanceUnchanged()
        {
            var (connection, tx) = Begin();
            using (connection)
            {
                using (tx)
                {
                    await _vendorRepository.UpdateVendorBalance(_vendorId, 1234.56m, tx);
                    tx.Rollback();
                }

                Assert.Equal(0m, await connection.ExecuteScalarAsync<decimal>(
                    "SELECT balance FROM vendor WHERE id = @Id", new { Id = _vendorId }));
            }
        }

        // ---- GetAllAp. As-of dates below are relative to the seeded due date 2026-12-01. ----
        // Vendor: open bill (partial, balance 2500) and paid bill (balance 0), both due 2026-12-01.
        // Other vendor: one unpaid bill (700) due 2026-12-01.

        private static readonly DateTime BeforeDue = new(2026, 11, 8);
        private static readonly DateTime AfterDue = new(2026, 12, 2);

        private Task<PagedList<ApAgingDto>> GetAllApAsync(string? status = null, long? vendorId = null, DateTime? asOf = null,
            int pageNumber = 1, int pageSize = 100) =>
            _repository.GetAllAp(new ApAgingParameters
            {
                Status = status, VendorId = vendorId ?? _vendorId, AsOfDate = asOf ?? BeforeDue,
                PageNumber = pageNumber, PageSize = pageSize
            });

        [Fact]
        public async Task GetAllAp_NoStatus_ReturnsEveryBillOfTheVendorPaidIncluded()
        {
            var result = await GetAllApAsync();

            Assert.Equal(new[] { _openBillId, _paidBillId }.OrderBy(x => x), result.Select(r => r.Id).OrderBy(x => x));
            Assert.Equal(2, result.MetaData.TotalCount);
        }

        [Fact]
        public async Task GetAllAp_ReturnsStoredColumnsAndVendorName()
        {
            var item = (await GetAllApAsync()).Single(r => r.Id == _openBillId);

            Assert.StartsWith($"AP-{_marker}-", item.Number);
            Assert.Equal(_vendorId, item.VendorId);
            Assert.Equal($"{_marker} Vendor", item.Vendor_Name);
            Assert.Equal(new DateTime(2026, 12, 1), item.Due_Date);
            Assert.Equal(4000m, item.Amount);
            Assert.Equal(1500m, item.Paid);
            Assert.Equal(2500m, item.Balance);
            Assert.Equal("partial", item.Status);
        }

        [Fact]
        public async Task GetAllAp_LeavesCalculatedFieldsToTheService()
        {
            var item = (await GetAllApAsync()).Single(r => r.Id == _openBillId);

            Assert.Equal(0, item.Days_Overdue);
            Assert.Equal(string.Empty, item.Aging_Status);
        }

        [Fact]
        public async Task GetAllAp_VendorFilter_ExcludesOtherVendors()
        {
            var result = await GetAllApAsync(vendorId: _otherVendorId);

            Assert.Equal(_otherVendorBillId, Assert.Single(result).Id);
        }

        [Fact]
        public async Task GetAllAp_VendorWithoutBills_ReturnsEmpty()
        {
            var result = await GetAllApAsync(vendorId: _emptyVendorId);

            Assert.Empty(result);
            Assert.Equal(0, result.MetaData.TotalCount);
        }

        [Fact]
        public async Task GetAllAp_Overdue_OpenBillsPastDueOnly()
        {
            var result = await GetAllApAsync("Overdue", asOf: AfterDue);

            Assert.Equal(_openBillId, Assert.Single(result).Id);
        }

        [Fact]
        public async Task GetAllAp_Overdue_NothingBeforeTheDueDate()
        {
            Assert.Empty(await GetAllApAsync("Overdue", asOf: BeforeDue));
        }

        [Fact]
        public async Task GetAllAp_Overdue_NotOnTheDueDateItself()
        {
            Assert.Empty(await GetAllApAsync("Overdue", asOf: new DateTime(2026, 12, 1)));
        }

        [Fact]
        public async Task GetAllAp_Partial_StoredStatusAndNotPastDue()
        {
            Assert.Equal(_openBillId, Assert.Single(await GetAllApAsync("Partial", asOf: BeforeDue)).Id);
            Assert.Empty(await GetAllApAsync("Partial", asOf: AfterDue));
        }

        [Fact]
        public async Task GetAllAp_Unpaid_StoredStatusIsCaseInsensitive()
        {
            var result = await GetAllApAsync("UNPAID", vendorId: _otherVendorId, asOf: BeforeDue);

            Assert.Equal(_otherVendorBillId, Assert.Single(result).Id);
        }

        [Fact]
        public async Task GetAllAp_Paid_BalanceZeroOrLess()
        {
            var result = await GetAllApAsync("Paid", asOf: AfterDue);

            Assert.Equal(_paidBillId, Assert.Single(result).Id);
        }

        [Fact]
        public async Task GetAllAp_UnknownStatus_IsIgnored()
        {
            Assert.Equal(2, (await GetAllApAsync("whatever")).Count);
        }

        [Fact]
        public async Task GetAllAp_OrderedByDueDate()
        {
            var result = await GetAllApAsync();

            Assert.Equal(result.OrderBy(r => r.Due_Date).Select(r => r.Due_Date), result.Select(r => r.Due_Date));
        }

        [Fact]
        public async Task GetAllAp_Paging_SplitsRowsAndKeepsTotalCount()
        {
            var first = await GetAllApAsync(pageNumber: 1, pageSize: 1);
            var second = await GetAllApAsync(pageNumber: 2, pageSize: 1);

            Assert.Single(first);
            Assert.Single(second);
            Assert.NotEqual(first[0].Id, second[0].Id);
            Assert.Equal(2, first.MetaData.TotalCount);
            Assert.Equal(2, first.MetaData.TotalPages);
            Assert.Equal(2, second.MetaData.CurrentPage);
        }

        // ---- GetApById ----

        [Fact]
        public async Task GetApById_Existing_ReturnsBillColumnsAndVendorName()
        {
            var ap = await _repository.GetApById(_openBillId);

            Assert.NotNull(ap);
            Assert.Equal(_openBillId, ap!.Id);
            Assert.StartsWith($"AP-{_marker}-", ap.Number);
            Assert.Equal(_vendorId, ap.VendorId);
            Assert.Equal($"{_marker} Vendor", ap.Vendor);
            Assert.Equal(_vehicleId, ap.VehicleId);
            Assert.Equal(new DateTime(2026, 10, 1), ap.Date);
            Assert.Equal(new DateTime(2026, 12, 1), ap.DueDate);
            Assert.Equal(4000m, ap.Amount);
            Assert.Equal(1500m, ap.Paid);
            Assert.Equal(2500m, ap.Balance);
            Assert.Equal("partial", ap.Status);
        }

        [Fact]
        public async Task GetApById_ReturnsTheVendorContactDetails()
        {
            using (IDbConnection connection = _repositoryContext.GetDatabaseConnection())
                await connection.ExecuteAsync(
                    "UPDATE vendor SET contact = 'Jo Vendor', phone = '555-0100', email = 'jo@example.com', address = '1 Main St' WHERE id = @Id",
                    new { Id = _vendorId });

            var ap = await _repository.GetApById(_openBillId);

            Assert.Equal("Jo Vendor", ap!.Contact);
            Assert.Equal("555-0100", ap.Phone);
            Assert.Equal("jo@example.com", ap.Email);
            Assert.Equal("1 Main St", ap.Address);
        }

        [Fact]
        public async Task GetApById_VendorWithoutContactDetails_ReturnsNulls()
        {
            var ap = await _repository.GetApById(_otherVendorBillId);

            Assert.Null(ap!.Contact);
            Assert.Null(ap.Phone);
            Assert.Null(ap.Email);
            Assert.Null(ap.Address);
        }

        [Fact]
        public async Task GetApById_BillWithoutVehicle_VehicleIdIsNull()
        {
            Assert.Null((await _repository.GetApById(_paidBillId))!.VehicleId);
        }

        [Fact]
        public async Task GetApById_ReturnsOnlyTheRequestedBill()
        {
            var ap = await _repository.GetApById(_otherVendorBillId);

            Assert.Equal(_otherVendorBillId, ap!.Id);
            Assert.Equal(_otherVendorId, ap.VendorId);
            Assert.Equal(700m, ap.Amount);
        }

        [Fact]
        public async Task GetApById_LeavesDaysToTheService()
        {
            Assert.Equal(0, (await _repository.GetApById(_openBillId))!.Days);
        }

        [Fact]
        public async Task GetApById_UnknownId_ReturnsNull()
        {
            Assert.Null(await _repository.GetApById(long.MaxValue));
        }

        [Fact]
        public async Task GetAllAp_PageBeyondTheEnd_ReturnsEmptyWithTotalCount()
        {
            var result = await GetAllApAsync(pageNumber: 5, pageSize: 1);

            Assert.Empty(result);
            Assert.Equal(2, result.MetaData.TotalCount);
        }
    }
}
