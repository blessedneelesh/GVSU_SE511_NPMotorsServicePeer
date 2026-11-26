using NPMotors.Repository.Repositories;

namespace NPMotors.Repository.Tests.DataAccess
{
    // Story 27: ReportRepository.GetSoldVehicleCosts. Test period: March 2091.
    public class ReportCogsRepositoryTest : TestBase, IAsyncLifetime
    {
        private static readonly DateTime Start = new(2091, 3, 1);
        private static readonly DateTime End = new(2091, 3, 31);
        private static readonly DateTime InPeriod = new(2091, 3, 15);

        private readonly ReportRepository _repository;
        private readonly RevenueSeed _seed;

        public ReportCogsRepositoryTest()
        {
            _repository = new ReportRepository(_repositoryContext);
            _seed = new RevenueSeed(() => _repositoryContext.GetDatabaseConnection());
        }

        public Task InitializeAsync() => _seed.InitializeAsync();

        public Task DisposeAsync() => _seed.CleanupAsync();

        // ---- AC1: posted invoices, once each ----

        [Fact]
        public async Task GetSoldVehicleCosts_PostedInvoice_ReturnsTheVehicleAndInvoiceColumns()
        {
            var vehicleId = await _seed.AddVehicleAsync(24000m, 1200m);
            var invoiceId = await _seed.AddInvoiceAsync(InPeriod, 30000m, vehicleId: vehicleId);

            var rows = await _repository.GetSoldVehicleCosts(Start, End);

            var row = Assert.Single(rows);
            Assert.Equal(vehicleId, row.Vehicle_Id);
            Assert.Equal(17, row.Vin.Length);
            Assert.Equal(invoiceId, row.Invoice_Id);
            Assert.StartsWith(_seed.Marker, row.Invoice_Number);
            Assert.Equal(InPeriod, row.Invoice_Date);
            Assert.Equal(25200m, row.Total_Cost);
        }

        [Fact]
        public async Task GetSoldVehicleCosts_TotalCostIsPurchasePricePlusAdditionalCosts()
        {
            var vehicleId = await _seed.AddVehicleAsync(10000m, 1000m);
            await _seed.AddInvoiceAsync(InPeriod, 14000m, vehicleId: vehicleId);

            var row = Assert.Single(await _repository.GetSoldVehicleCosts(Start, End));

            Assert.Equal(11000m, row.Total_Cost);
        }

        [Fact]
        public async Task GetSoldVehicleCosts_SeveralVehicles_AreAllReturnedOnceEach()
        {
            var first = await _seed.AddVehicleAsync(10000m);
            var second = await _seed.AddVehicleAsync(20000m, 500m);
            await _seed.AddInvoiceAsync(InPeriod, 12000m, vehicleId: first);
            await _seed.AddInvoiceAsync(new DateTime(2091, 3, 20), 22000m, vehicleId: second);

            var rows = await _repository.GetSoldVehicleCosts(Start, End);

            Assert.Equal(2, rows.Count);
            Assert.Equal(new[] { first, second }, rows.Select(r => r.Vehicle_Id).ToArray());
            Assert.Equal(new[] { 10000m, 20500m }, rows.Select(r => r.Total_Cost).ToArray());
        }

        [Fact]
        public async Task GetSoldVehicleCosts_VehicleWithDraftAndPostedInvoice_AppearsOnce()
        {
            var vehicleId = await _seed.AddVehicleAsync(10000m);
            await _seed.AddInvoiceAsync(InPeriod, 12000m, lifecycle: "draft", vehicleId: vehicleId);
            await _seed.AddInvoiceAsync(InPeriod, 12000m, vehicleId: vehicleId);

            var row = Assert.Single(await _repository.GetSoldVehicleCosts(Start, End));

            Assert.Equal(vehicleId, row.Vehicle_Id);
        }

        [Theory]
        [InlineData("unpaid")]
        [InlineData("partial")]
        [InlineData("paid")]
        public async Task GetSoldVehicleCosts_AnyPaymentStatus_IsIncluded(string paymentStatus)
        {
            var vehicleId = await _seed.AddVehicleAsync(10000m);
            await _seed.AddInvoiceAsync(InPeriod, 12000m, paymentStatus: paymentStatus, vehicleId: vehicleId);

            Assert.Single(await _repository.GetSoldVehicleCosts(Start, End));
        }

        [Fact]
        public async Task GetSoldVehicleCosts_DraftInvoice_IsExcluded()
        {
            var vehicleId = await _seed.AddVehicleAsync(10000m);
            await _seed.AddInvoiceAsync(InPeriod, 12000m, lifecycle: "draft", vehicleId: vehicleId);

            Assert.Empty(await _repository.GetSoldVehicleCosts(Start, End));
        }

        // ---- AC2: unsold inventory and AP payments create no COGS ----

        [Fact]
        public async Task GetSoldVehicleCosts_UnsoldVehicle_IsExcluded()
        {
            await _seed.AddVehicleAsync(10000m);

            Assert.Empty(await _repository.GetSoldVehicleCosts(Start, End));
        }

        [Fact]
        public async Task GetSoldVehicleCosts_UnsoldVehicleWithPaidApBill_IsExcluded()
        {
            await _seed.AddVehicleAsync(10000m);
            await _seed.AddApBillAsync(10000m, paid: 10000m);

            Assert.Empty(await _repository.GetSoldVehicleCosts(Start, End));
        }

        [Fact]
        public async Task GetSoldVehicleCosts_PayingTheVendor_DoesNotChangeTheCost()
        {
            var vehicleId = await _seed.AddVehicleAsync(10000m, 500m);
            await _seed.AddInvoiceAsync(InPeriod, 14000m, vehicleId: vehicleId);
            await _seed.AddApBillAsync(10000m, paid: 4000m);
            await _seed.AddApBillAsync(500m, paid: 500m);

            var row = Assert.Single(await _repository.GetSoldVehicleCosts(Start, End));

            Assert.Equal(10500m, row.Total_Cost);
        }

        [Fact]
        public async Task GetSoldVehicleCosts_InvoiceWithoutAVehicle_IsExcluded()
        {
            await _seed.AddInvoiceAsync(InPeriod, 12000m);

            Assert.Empty(await _repository.GetSoldVehicleCosts(Start, End));
        }

        // ---- AC3: cancelled sales drop out ----

        [Fact]
        public async Task GetSoldVehicleCosts_CancelledInvoice_IsExcluded()
        {
            var vehicleId = await _seed.AddVehicleAsync(10000m);
            await _seed.AddInvoiceAsync(InPeriod, 12000m, lifecycle: "cancelled", vehicleId: vehicleId);

            Assert.Empty(await _repository.GetSoldVehicleCosts(Start, End));
        }

        [Fact]
        public async Task GetSoldVehicleCosts_CancelledThenResold_CountsTheCostOnce()
        {
            var vehicleId = await _seed.AddVehicleAsync(10000m);
            await _seed.AddInvoiceAsync(InPeriod, 12000m, lifecycle: "cancelled", vehicleId: vehicleId);
            await _seed.AddInvoiceAsync(new DateTime(2091, 3, 20), 12500m, vehicleId: vehicleId);

            var row = Assert.Single(await _repository.GetSoldVehicleCosts(Start, End));

            Assert.Equal(10000m, row.Total_Cost);
            Assert.Equal(new DateTime(2091, 3, 20), row.Invoice_Date);
        }

        // ---- Period and ordering ----

        [Fact]
        public async Task GetSoldVehicleCosts_StartAndEndDates_AreInclusive()
        {
            var first = await _seed.AddVehicleAsync(100m);
            var second = await _seed.AddVehicleAsync(200m);
            await _seed.AddInvoiceAsync(Start, 150m, vehicleId: first);
            await _seed.AddInvoiceAsync(End, 250m, vehicleId: second);

            Assert.Equal(2, (await _repository.GetSoldVehicleCosts(Start, End)).Count);
        }

        [Fact]
        public async Task GetSoldVehicleCosts_InvoicesOutsideThePeriod_AreExcluded()
        {
            var before = await _seed.AddVehicleAsync(100m);
            var after = await _seed.AddVehicleAsync(200m);
            await _seed.AddInvoiceAsync(Start.AddDays(-1), 150m, vehicleId: before);
            await _seed.AddInvoiceAsync(End.AddDays(1), 250m, vehicleId: after);

            Assert.Empty(await _repository.GetSoldVehicleCosts(Start, End));
        }

        [Fact]
        public async Task GetSoldVehicleCosts_EndWithTimeOfDay_StillIncludesThatDay()
        {
            var vehicleId = await _seed.AddVehicleAsync(100m);
            await _seed.AddInvoiceAsync(End, 150m, vehicleId: vehicleId);

            Assert.Single(await _repository.GetSoldVehicleCosts(Start, End.AddHours(15)));
        }

        [Fact]
        public async Task GetSoldVehicleCosts_AreOrderedByInvoiceDate()
        {
            var late = await _seed.AddVehicleAsync(100m);
            var early = await _seed.AddVehicleAsync(200m);
            await _seed.AddInvoiceAsync(new DateTime(2091, 3, 25), 150m, vehicleId: late);
            await _seed.AddInvoiceAsync(new DateTime(2091, 3, 5), 250m, vehicleId: early);

            var rows = await _repository.GetSoldVehicleCosts(Start, End);

            Assert.Equal(new[] { early, late }, rows.Select(r => r.Vehicle_Id).ToArray());
        }

        [Fact]
        public async Task GetSoldVehicleCosts_NoData_ReturnsEmptyListNotNull()
        {
            var rows = await _repository.GetSoldVehicleCosts(Start, End);

            Assert.NotNull(rows);
            Assert.Empty(rows);
        }
    }
}
