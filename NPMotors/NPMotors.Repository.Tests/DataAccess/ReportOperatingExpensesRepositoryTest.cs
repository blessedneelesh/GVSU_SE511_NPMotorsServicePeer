using Dapper;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.Repositories;

namespace NPMotors.Repository.Tests.DataAccess
{
    // Story 28: ReportRepository.GetEmployeePayroll and GetExpenseCandidates. Test period: March 2092.
    // Payroll rows are filtered to the seeded employees (the employee table is shared); candidates are isolated by date.
    public class ReportOperatingExpensesRepositoryTest : TestBase, IAsyncLifetime
    {
        private static readonly DateTime Start = new(2092, 3, 1);
        private static readonly DateTime End = new(2092, 3, 31);
        private static readonly DateTime InPeriod = new(2092, 3, 15);

        private readonly ReportRepository _repository;
        private readonly RevenueSeed _seed;

        public ReportOperatingExpensesRepositoryTest()
        {
            _repository = new ReportRepository(_repositoryContext);
            _seed = new RevenueSeed(() => _repositoryContext.GetDatabaseConnection());
        }

        public Task InitializeAsync() => _seed.InitializeAsync();

        public Task DisposeAsync() => _seed.CleanupAsync();

        private async Task<List<EmployeePayrollDto>> SeededPayrollAsync() =>
            (await _repository.GetEmployeePayroll(Start, End)).Where(e => e.Employee_Name.StartsWith(_seed.Marker)).ToList();

        // ---- GetEmployeePayroll (AC1) ----

        [Fact]
        public async Task GetEmployeePayroll_ActiveEmployee_ReturnsSalaryBonusAndRate()
        {
            var id = await _seed.AddEmployeeAsync(120000m, 6000m, 5m);

            var row = Assert.Single(await SeededPayrollAsync());

            Assert.Equal(id, row.Employee_Id);
            Assert.StartsWith(_seed.Marker, row.Employee_Name);
            Assert.EndsWith("Emp1", row.Employee_Name);
            Assert.Equal(120000m, row.Salary);
            Assert.Equal(6000m, row.Bonus);
            Assert.Equal(5m, row.Commission_Rate);
        }

        [Fact]
        public async Task GetEmployeePayroll_InactiveEmployee_IsExcluded()
        {
            await _seed.AddEmployeeAsync(120000m, 0m, 5m, status: "inactive");

            Assert.Empty(await SeededPayrollAsync());
        }

        [Fact]
        public async Task GetEmployeePayroll_StatusComparisonIgnoresCase()
        {
            await _seed.AddEmployeeAsync(120000m, 0m, 5m, status: "Active");

            Assert.Single(await SeededPayrollAsync());
        }

        [Fact]
        public async Task GetEmployeePayroll_EmployeeWithoutStatus_IsExcluded()
        {
            using var c = _repositoryContext.GetDatabaseConnection();
            await c.ExecuteAsync("INSERT INTO employee (first_name, last_name, salary) VALUES (@Name, 'NoStatus', 1000)", new { Name = _seed.Marker });

            Assert.Empty(await SeededPayrollAsync());
        }

        [Fact]
        public async Task GetEmployeePayroll_NullSalaryBonusAndCommission_AreReturnedAsZero()
        {
            await _seed.AddEmployeeAsync(null, null, null);

            var row = Assert.Single(await SeededPayrollAsync());

            Assert.Equal(0m, row.Salary);
            Assert.Equal(0m, row.Bonus);
            Assert.Equal(0m, row.Commission_Rate);
            Assert.Equal(0m, row.Commission_Sales);
        }

        [Fact]
        public async Task GetEmployeePayroll_CommissionSales_SumsThePostedInvoicesOfThatSalesperson()
        {
            var id = await _seed.AddEmployeeAsync(0m, 0m, 5m);
            await _seed.AddInvoiceAsync(InPeriod, 30000m, salespersonId: id);
            await _seed.AddInvoiceAsync(new DateTime(2092, 3, 20), 12000m, fees: 500m, discount: 100m, tax: 900m, salespersonId: id);

            var row = Assert.Single(await SeededPayrollAsync());

            Assert.Equal(42000m, row.Commission_Sales);
        }

        [Fact]
        public async Task GetEmployeePayroll_CommissionSales_IgnoreDraftAndCancelledInvoices()
        {
            var id = await _seed.AddEmployeeAsync(0m, 0m, 5m);
            await _seed.AddInvoiceAsync(InPeriod, 30000m, lifecycle: "draft", salespersonId: id);
            await _seed.AddInvoiceAsync(InPeriod, 20000m, lifecycle: "cancelled", salespersonId: id);
            await _seed.AddInvoiceAsync(InPeriod, 1000m, salespersonId: id);

            var row = Assert.Single(await SeededPayrollAsync());

            Assert.Equal(1000m, row.Commission_Sales);
        }

        [Theory]
        [InlineData("unpaid")]
        [InlineData("partial")]
        [InlineData("paid")]
        public async Task GetEmployeePayroll_CommissionSales_CountAnyPaymentStatus(string paymentStatus)
        {
            var id = await _seed.AddEmployeeAsync(0m, 0m, 5m);
            await _seed.AddInvoiceAsync(InPeriod, 1000m, paymentStatus: paymentStatus, salespersonId: id);

            Assert.Equal(1000m, Assert.Single(await SeededPayrollAsync()).Commission_Sales);
        }

        [Fact]
        public async Task GetEmployeePayroll_CommissionSales_OnlyCountTheEmployeesOwnInvoices()
        {
            var first = await _seed.AddEmployeeAsync(0m, 0m, 5m);
            var second = await _seed.AddEmployeeAsync(0m, 0m, 2m);
            await _seed.AddInvoiceAsync(InPeriod, 1000m, salespersonId: first);
            await _seed.AddInvoiceAsync(InPeriod, 4000m, salespersonId: second);

            var rows = await SeededPayrollAsync();

            Assert.Equal(1000m, rows.Single(r => r.Employee_Id == first).Commission_Sales);
            Assert.Equal(4000m, rows.Single(r => r.Employee_Id == second).Commission_Sales);
        }

        [Fact]
        public async Task GetEmployeePayroll_CommissionSales_StartAndEndAreInclusiveAndOutsideIsExcluded()
        {
            var id = await _seed.AddEmployeeAsync(0m, 0m, 5m);
            await _seed.AddInvoiceAsync(Start, 100m, salespersonId: id);
            await _seed.AddInvoiceAsync(End, 200m, salespersonId: id);
            await _seed.AddInvoiceAsync(Start.AddDays(-1), 400m, salespersonId: id);
            await _seed.AddInvoiceAsync(End.AddDays(1), 800m, salespersonId: id);

            Assert.Equal(300m, Assert.Single(await SeededPayrollAsync()).Commission_Sales);
        }

        [Fact]
        public async Task GetEmployeePayroll_EmployeeWithNoSales_IsStillReturnedWithZeroCommissionSales()
        {
            await _seed.AddEmployeeAsync(60000m, 0m, 5m);

            Assert.Equal(0m, Assert.Single(await SeededPayrollAsync()).Commission_Sales);
        }

        [Fact]
        public async Task GetEmployeePayroll_SalaryAndBonusDoNotDependOnThePeriod()
        {
            await _seed.AddEmployeeAsync(120000m, 6000m, 0m);

            var other = (await _repository.GetEmployeePayroll(new DateTime(2000, 1, 1), new DateTime(2000, 1, 31)))
                .Single(e => e.Employee_Name.StartsWith(_seed.Marker));

            Assert.Equal(120000m, other.Salary);
            Assert.Equal(6000m, other.Bonus);
        }

        // ---- GetExpenseCandidates: payments (AC2, AC3, AC4) ----

        [Fact]
        public async Task GetExpenseCandidates_PostedPayment_ReturnsItsColumns()
        {
            var (paymentId, _) = await _seed.AddPaymentAsync(InPeriod, 500m, "Office Expense");

            var row = Assert.Single(await _repository.GetExpenseCandidates(Start, End));

            Assert.Equal("Payment", row.Source_Type);
            Assert.Equal(paymentId, row.Source_Id);
            Assert.StartsWith(_seed.Marker, row.Source_Number);
            Assert.Equal(InPeriod, row.Date);
            Assert.Equal(500m, row.Amount);
            Assert.Equal("Office Expense", row.Payment_Category);
            Assert.Null(row.Adjustment_Type);
            Assert.Null(row.Ap_Bill_Id);
            Assert.Null(row.Vehicle_Id);
            Assert.False(row.Is_Reversed);
        }

        [Fact]
        public async Task GetExpenseCandidates_PaymentLinkedToApBill_ReturnsTheBillId()
        {
            var billId = await _seed.AddApBillAsync(8000m, 8000m);
            await _seed.AddPaymentAsync(InPeriod, 8000m, null, apBillId: billId);

            var row = Assert.Single(await _repository.GetExpenseCandidates(Start, End));

            Assert.Equal(billId, row.Ap_Bill_Id);
        }

        [Fact]
        public async Task GetExpenseCandidates_PaymentLinkedToVehicle_ReturnsTheVehicleId()
        {
            var vehicleId = await _seed.AddVehicleAsync(24000m);
            await _seed.AddPaymentAsync(InPeriod, 24000m, null, vehicleId: vehicleId);

            var row = Assert.Single(await _repository.GetExpenseCandidates(Start, End));

            Assert.Equal(vehicleId, row.Vehicle_Id);
        }

        [Fact]
        public async Task GetExpenseCandidates_PaymentNotPosted_IsExcluded()
        {
            await _seed.AddPaymentAsync(InPeriod, 500m, "Office Expense", status: "Voided");

            Assert.Empty(await _repository.GetExpenseCandidates(Start, End));
        }

        [Fact]
        public async Task GetExpenseCandidates_PaymentWhoseTransactionIsNotPosted_IsExcluded()
        {
            await _seed.AddPaymentAsync(InPeriod, 500m, "Office Expense", transactionStatus: "pending");

            Assert.Empty(await _repository.GetExpenseCandidates(Start, End));
        }

        [Fact]
        public async Task GetExpenseCandidates_ReversedPayment_IsReturnedFlaggedAsReversed()
        {
            var (_, transactionId) = await _seed.AddPaymentAsync(InPeriod, 500m, "Office Expense");
            await _seed.AddReversalAsync(transactionId, InPeriod, 500m);

            var row = Assert.Single(await _repository.GetExpenseCandidates(Start, End));

            Assert.True(row.Is_Reversed);
        }

        [Fact]
        public async Task GetExpenseCandidates_ReversalTransactionItself_IsNotACandidate()
        {
            var (_, transactionId) = await _seed.AddPaymentAsync(InPeriod, 500m, "Office Expense");
            await _seed.AddReversalAsync(transactionId, InPeriod, 500m);

            Assert.Single(await _repository.GetExpenseCandidates(Start, End));
        }

        [Fact]
        public async Task GetExpenseCandidates_PaymentDates_StartAndEndAreInclusiveAndOutsideIsExcluded()
        {
            await _seed.AddPaymentAsync(Start, 100m, "Rent");
            await _seed.AddPaymentAsync(End, 200m, "Rent");
            await _seed.AddPaymentAsync(Start.AddDays(-1), 400m, "Rent");
            await _seed.AddPaymentAsync(End.AddDays(1), 800m, "Rent");

            var rows = await _repository.GetExpenseCandidates(Start, End);

            Assert.Equal(new[] { 100m, 200m }, rows.Select(r => r.Amount).ToArray());
        }

        // ---- GetExpenseCandidates: adjustments (AC2, AC5) ----

        [Fact]
        public async Task GetExpenseCandidates_PostedOutgoingMatchedAdjustment_ReturnsItsColumns()
        {
            var transactionId = await _seed.AddAdjustmentAsync(InPeriod, 125m, "Bank Service Charge", direction: "out");

            var row = Assert.Single(await _repository.GetExpenseCandidates(Start, End));

            Assert.Equal("Adjustment", row.Source_Type);
            Assert.Equal(transactionId, row.Source_Id);
            Assert.StartsWith(_seed.Marker, row.Source_Number);
            Assert.Equal(InPeriod, row.Date);
            Assert.Equal(125m, row.Amount);
            Assert.Equal("Bank Service Charge", row.Adjustment_Type);
            Assert.Null(row.Payment_Category);
            Assert.Null(row.Ap_Bill_Id);
            Assert.Null(row.Vehicle_Id);
            Assert.False(row.Is_Reversed);
        }

        [Fact]
        public async Task GetExpenseCandidates_ReversedAdjustment_IsReturnedFlaggedAsReversed()
        {
            var transactionId = await _seed.AddAdjustmentAsync(InPeriod, 125m, "Bank Service Charge", direction: "out");
            await _seed.AddReversalAsync(transactionId, InPeriod, 125m);

            var row = Assert.Single(await _repository.GetExpenseCandidates(Start, End));

            Assert.True(row.Is_Reversed);
        }

        [Fact]
        public async Task GetExpenseCandidates_IncomingAdjustment_IsExcluded()
        {
            await _seed.AddAdjustmentAsync(InPeriod, 625m, "Interest Received", direction: "in");

            Assert.Empty(await _repository.GetExpenseCandidates(Start, End));
        }

        [Fact]
        public async Task GetExpenseCandidates_AdjustmentTransactionNotPosted_IsExcluded()
        {
            await _seed.AddAdjustmentAsync(InPeriod, 125m, "Bank Service Charge", direction: "out", status: "pending");

            Assert.Empty(await _repository.GetExpenseCandidates(Start, End));
        }

        [Fact]
        public async Task GetExpenseCandidates_StatementOnlyUnmatchedLine_IsExcluded()
        {
            using var c = _repositoryContext.GetDatabaseConnection();
            await c.ExecuteAsync(
                @"INSERT INTO bank_statement_line (bank_account_id, posted_date, description, type, adjustment_type, direction, deposit, withdrawal,
                                                   running_balance, statement_status, match_status, source)
                  VALUES (@AccountId, @Date, 'seed', 'BankAdjustment', 'Bank Service Charge', 'out'::direction_enum, 0, 125, 0, 'Cleared', 'Unmatched', 'Manual')",
                new { AccountId = _seed.AccountId, Date = InPeriod });

            Assert.Empty(await _repository.GetExpenseCandidates(Start, End));
        }

        [Fact]
        public async Task GetExpenseCandidates_OutgoingTransferWithoutAdjustmentType_IsExcluded()
        {
            await _seed.AddAdjustmentAsync(InPeriod, 5000m, null, direction: "out", type: "Transfer");

            Assert.Empty(await _repository.GetExpenseCandidates(Start, End));
        }

        [Fact]
        public async Task GetExpenseCandidates_AdjustmentDates_StartAndEndAreInclusiveAndOutsideIsExcluded()
        {
            await _seed.AddAdjustmentAsync(Start, 10m, "Bank Service Charge", direction: "out");
            await _seed.AddAdjustmentAsync(End, 20m, "Bank Service Charge", direction: "out");
            await _seed.AddAdjustmentAsync(Start.AddDays(-1), 40m, "Bank Service Charge", direction: "out");
            await _seed.AddAdjustmentAsync(End.AddDays(1), 80m, "Bank Service Charge", direction: "out");

            var rows = await _repository.GetExpenseCandidates(Start, End);

            Assert.Equal(new[] { 10m, 20m }, rows.Select(r => r.Amount).ToArray());
        }

        // ---- Both parts together ----

        [Fact]
        public async Task GetExpenseCandidates_PaymentsAndAdjustments_AreReturnedTogetherOrderedByDate()
        {
            await _seed.AddAdjustmentAsync(new DateTime(2092, 3, 25), 125m, "Bank Service Charge", direction: "out");
            await _seed.AddPaymentAsync(new DateTime(2092, 3, 5), 500m, "Office Expense");
            await _seed.AddPaymentAsync(new DateTime(2092, 3, 30), 300m, "Rent");

            var rows = await _repository.GetExpenseCandidates(Start, End);

            Assert.Equal(new[] { "Payment", "Adjustment", "Payment" }, rows.Select(r => r.Source_Type).ToArray());
            Assert.Equal(new[] { 500m, 125m, 300m }, rows.Select(r => r.Amount).ToArray());
        }

        [Fact]
        public async Task GetExpenseCandidates_NoData_ReturnsEmptyListNotNull()
        {
            var rows = await _repository.GetExpenseCandidates(Start, End);

            Assert.NotNull(rows);
            Assert.Empty(rows);
        }
    }
}
