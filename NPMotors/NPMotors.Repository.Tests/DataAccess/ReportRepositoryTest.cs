using NPMotors.Repository.Repositories;

namespace NPMotors.Repository.Tests.DataAccess
{
    // Story 26: ReportRepository.GetSalesRevenue and GetIncomeAdjustments. Test period: March 2090.
    public class ReportRepositoryTest : TestBase, IAsyncLifetime
    {
        private static readonly DateTime Start = new(2090, 3, 1);
        private static readonly DateTime End = new(2090, 3, 31);
        private static readonly DateTime InPeriod = new(2090, 3, 15);

        private readonly ReportRepository _repository;
        private readonly RevenueSeed _seed;

        public ReportRepositoryTest()
        {
            _repository = new ReportRepository(_repositoryContext);
            _seed = new RevenueSeed(() => _repositoryContext.GetDatabaseConnection());
        }

        public Task InitializeAsync() => _seed.InitializeAsync();

        public Task DisposeAsync() => _seed.CleanupAsync();

        // ---- GetSalesRevenue (AC1, AC2) ----

        [Fact]
        public async Task GetSalesRevenue_NoInvoices_ReturnsZeros()
        {
            var result = await _repository.GetSalesRevenue(Start, End);

            Assert.Equal(0m, result.Vehicle_Revenue);
            Assert.Equal(0m, result.Service_Revenue);
            Assert.Equal(0m, result.Fee_Income);
            Assert.Equal(0m, result.Discounts);
            Assert.Equal(0, result.Invoice_Count);
        }

        [Fact]
        public async Task GetSalesRevenue_PostedInvoice_SumsPriceFeesAndDiscount()
        {
            await _seed.AddInvoiceAsync(InPeriod, 30000m, fees: 500m, discount: 1000m, tax: 2400m);

            var result = await _repository.GetSalesRevenue(Start, End);

            Assert.Equal(30000m, result.Vehicle_Revenue);
            Assert.Equal(500m, result.Fee_Income);
            Assert.Equal(1000m, result.Discounts);
            Assert.Equal(1, result.Invoice_Count);
        }

        [Fact]
        public async Task GetSalesRevenue_SeveralInvoices_AreSummed()
        {
            await _seed.AddInvoiceAsync(InPeriod, 30000m, fees: 500m, discount: 1000m);
            await _seed.AddInvoiceAsync(new DateTime(2090, 3, 20), 12000m, fees: 250m, discount: 200m);

            var result = await _repository.GetSalesRevenue(Start, End);

            Assert.Equal(42000m, result.Vehicle_Revenue);
            Assert.Equal(750m, result.Fee_Income);
            Assert.Equal(1200m, result.Discounts);
            Assert.Equal(2, result.Invoice_Count);
        }

        [Fact]
        public async Task GetSalesRevenue_ServiceRevenueIsZero()
        {
            await _seed.AddInvoiceAsync(InPeriod, 30000m);

            var result = await _repository.GetSalesRevenue(Start, End);

            Assert.Equal(0m, result.Service_Revenue);
        }

        [Fact]
        public async Task GetSalesRevenue_DraftInvoice_IsExcluded()
        {
            await _seed.AddInvoiceAsync(InPeriod, 30000m, lifecycle: "draft");

            var result = await _repository.GetSalesRevenue(Start, End);

            Assert.Equal(0m, result.Vehicle_Revenue);
            Assert.Equal(0, result.Invoice_Count);
        }

        [Fact]
        public async Task GetSalesRevenue_CancelledInvoice_IsExcluded()
        {
            await _seed.AddInvoiceAsync(InPeriod, 30000m, lifecycle: "cancelled");

            var result = await _repository.GetSalesRevenue(Start, End);

            Assert.Equal(0m, result.Vehicle_Revenue);
            Assert.Equal(0, result.Invoice_Count);
        }

        [Theory]
        [InlineData("unpaid")]
        [InlineData("partial")]
        [InlineData("paid")]
        public async Task GetSalesRevenue_AnyPaymentStatus_IsCounted(string paymentStatus)
        {
            await _seed.AddInvoiceAsync(InPeriod, 30000m, paymentStatus: paymentStatus);

            var result = await _repository.GetSalesRevenue(Start, End);

            Assert.Equal(30000m, result.Vehicle_Revenue);
            Assert.Equal(1, result.Invoice_Count);
        }

        [Fact]
        public async Task GetSalesRevenue_Tax_IsNeverIncluded()
        {
            await _seed.AddInvoiceAsync(InPeriod, 1000m, tax: 80m);

            var result = await _repository.GetSalesRevenue(Start, End);

            Assert.Equal(1000m, result.Vehicle_Revenue);
            Assert.Equal(0m, result.Fee_Income);
        }

        [Fact]
        public async Task GetSalesRevenue_StartAndEndDates_AreInclusive()
        {
            await _seed.AddInvoiceAsync(Start, 100m);
            await _seed.AddInvoiceAsync(End, 200m);

            var result = await _repository.GetSalesRevenue(Start, End);

            Assert.Equal(300m, result.Vehicle_Revenue);
            Assert.Equal(2, result.Invoice_Count);
        }

        [Fact]
        public async Task GetSalesRevenue_InvoicesOutsideThePeriod_AreExcluded()
        {
            await _seed.AddInvoiceAsync(Start.AddDays(-1), 100m);
            await _seed.AddInvoiceAsync(End.AddDays(1), 200m);

            var result = await _repository.GetSalesRevenue(Start, End);

            Assert.Equal(0m, result.Vehicle_Revenue);
            Assert.Equal(0, result.Invoice_Count);
        }

        [Fact]
        public async Task GetSalesRevenue_EndWithTimeOfDay_StillIncludesThatDay()
        {
            await _seed.AddInvoiceAsync(End, 200m);

            var result = await _repository.GetSalesRevenue(Start, End.AddHours(15));

            Assert.Equal(200m, result.Vehicle_Revenue);
        }

        // ---- GetIncomeAdjustments (AC3, AC4) ----

        [Fact]
        public async Task GetIncomeAdjustments_PostedIncomingAdjustment_IsReturnedWithItsColumns()
        {
            var transactionId = await _seed.AddAdjustmentAsync(InPeriod, 625m, "Interest Received");

            var rows = await _repository.GetIncomeAdjustments(Start, End);

            var row = Assert.Single(rows);
            Assert.Equal(transactionId, row.Transaction_Id);
            Assert.StartsWith(_seed.Marker, row.Number);
            Assert.Equal(InPeriod, row.Date);
            Assert.Equal(625m, row.Amount);
            Assert.Equal("Interest Received", row.Adjustment_Type);
        }

        [Fact]
        public async Task GetIncomeAdjustments_ReturnsBothInterestAndOtherTypes()
        {
            await _seed.AddAdjustmentAsync(InPeriod, 625m, "Interest Received");
            await _seed.AddAdjustmentAsync(InPeriod, 100m, "Other Adjustment");

            var rows = await _repository.GetIncomeAdjustments(Start, End);

            Assert.Equal(2, rows.Count);
            Assert.Contains(rows, r => r.Adjustment_Type == "Interest Received" && r.Amount == 625m);
            Assert.Contains(rows, r => r.Adjustment_Type == "Other Adjustment" && r.Amount == 100m);
        }

        [Fact]
        public async Task GetIncomeAdjustments_OutgoingAdjustment_IsExcluded()
        {
            await _seed.AddAdjustmentAsync(InPeriod, 50m, "Other Adjustment", direction: "out");

            Assert.Empty(await _repository.GetIncomeAdjustments(Start, End));
        }

        [Fact]
        public async Task GetIncomeAdjustments_TransactionNotPosted_IsExcluded()
        {
            await _seed.AddAdjustmentAsync(InPeriod, 625m, "Interest Received", status: "pending");

            Assert.Empty(await _repository.GetIncomeAdjustments(Start, End));
        }

        [Fact]
        public async Task GetIncomeAdjustments_StatementOnlyUnmatchedLine_IsExcluded()
        {
            await _seed.AddStatementOnlyAdjustmentAsync(InPeriod, 625m, "Interest Received");

            Assert.Empty(await _repository.GetIncomeAdjustments(Start, End));
        }

        [Fact]
        public async Task GetIncomeAdjustments_ReversedAdjustment_IsExcluded()
        {
            var transactionId = await _seed.AddAdjustmentAsync(InPeriod, 625m, "Interest Received");
            await _seed.AddReversalAsync(transactionId, InPeriod, 625m);

            Assert.Empty(await _repository.GetIncomeAdjustments(Start, End));
        }

        [Fact]
        public async Task GetIncomeAdjustments_ReversalOfAnotherAdjustment_DoesNotHideTheRest()
        {
            var reversed = await _seed.AddAdjustmentAsync(InPeriod, 625m, "Interest Received");
            await _seed.AddReversalAsync(reversed, InPeriod, 625m);
            await _seed.AddAdjustmentAsync(InPeriod, 100m, "Other Adjustment");

            var row = Assert.Single(await _repository.GetIncomeAdjustments(Start, End));
            Assert.Equal(100m, row.Amount);
        }

        [Theory]
        [InlineData("BankReceipt")]
        [InlineData("Transfer")]
        public async Task GetIncomeAdjustments_ArReceiptsAndTransfers_AreExcluded(string type)
        {
            await _seed.AddNonAdjustmentIncomingAsync(InPeriod, 5000m, type);

            Assert.Empty(await _repository.GetIncomeAdjustments(Start, End));
        }

        [Fact]
        public async Task GetIncomeAdjustments_StartAndEndDates_AreInclusive()
        {
            await _seed.AddAdjustmentAsync(Start, 10m, "Other Adjustment");
            await _seed.AddAdjustmentAsync(End, 20m, "Other Adjustment");

            Assert.Equal(2, (await _repository.GetIncomeAdjustments(Start, End)).Count);
        }

        [Fact]
        public async Task GetIncomeAdjustments_OutsideThePeriod_AreExcluded()
        {
            await _seed.AddAdjustmentAsync(Start.AddDays(-1), 10m, "Other Adjustment");
            await _seed.AddAdjustmentAsync(End.AddDays(1), 20m, "Other Adjustment");

            Assert.Empty(await _repository.GetIncomeAdjustments(Start, End));
        }

        [Fact]
        public async Task GetIncomeAdjustments_NoData_ReturnsEmptyListNotNull()
        {
            var rows = await _repository.GetIncomeAdjustments(Start, End);

            Assert.NotNull(rows);
            Assert.Empty(rows);
        }
    }
}
