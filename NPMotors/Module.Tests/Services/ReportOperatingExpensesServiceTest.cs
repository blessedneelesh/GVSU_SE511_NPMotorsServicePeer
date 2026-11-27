using System.ComponentModel.DataAnnotations;
using NPMotors.Repository.Interface;
using NPMotors.Repository.Models.Dto;
using NPMotors.Services.Service;

namespace Module.Tests.Services
{
    // Story 28, GetOperatingExpenses. CalculatePayroll and IsOperatingExpense are private static;
    // they are tested only through GetOperatingExpenses. ResolvePeriod is Story 26 (covered by ReportServiceTest).
    public class ReportOperatingExpensesServiceTest
    {
        // Hand-written fake: the test projects do not use a mocking library.
        private class FakeReportRepository : IReportRepository
        {
            public List<EmployeePayrollDto> Employees { get; set; } = new();
            public List<ExpenseCandidateDto> Candidates { get; set; } = new();
            public List<(DateTime Start, DateTime End)> PayrollCalls { get; } = new();
            public List<(DateTime Start, DateTime End)> CandidateCalls { get; } = new();

            public Task<List<EmployeePayrollDto>> GetEmployeePayroll(DateTime start, DateTime end)
            {
                PayrollCalls.Add((start, end));
                return Task.FromResult(Employees);
            }

            public Task<List<ExpenseCandidateDto>> GetExpenseCandidates(DateTime start, DateTime end)
            {
                CandidateCalls.Add((start, end));
                return Task.FromResult(Candidates);
            }

            // Story 26 and 27 members: not used by these tests.
            public Task<SalesRevenueTotalsDto> GetSalesRevenue(DateTime start, DateTime end) => throw new NotSupportedException();
            public Task<List<IncomeAdjustmentDto>> GetIncomeAdjustments(DateTime start, DateTime end) => throw new NotSupportedException();
            public Task<List<SoldVehicleCostDto>> GetSoldVehicleCosts(DateTime start, DateTime end) => throw new NotSupportedException();
        }

        private readonly FakeReportRepository _repository = new();

        private ReportService CreateService() => new(_repository);

        private static EmployeePayrollDto Employee(decimal salary, decimal bonus = 0m, decimal rate = 0m, decimal sales = 0m) => new()
        {
            Employee_Id = 1, Employee_Name = "Ann Lee", Salary = salary, Bonus = bonus, Commission_Rate = rate, Commission_Sales = sales
        };

        private static ExpenseCandidateDto Payment(decimal amount, string? category = "Office Expense", long? apBillId = null,
            long? vehicleId = null, bool reversed = false) => new()
        {
            Source_Type = "Payment", Source_Id = 1, Source_Number = "PAY-1", Date = new DateTime(2024, 3, 10), Amount = amount,
            Payment_Category = category, Ap_Bill_Id = apBillId, Vehicle_Id = vehicleId, Is_Reversed = reversed
        };

        private static ExpenseCandidateDto Adjustment(decimal amount, string type, bool reversed = false) => new()
        {
            Source_Type = "Adjustment", Source_Id = 2, Source_Number = "TXN-2", Date = new DateTime(2024, 3, 10), Amount = amount,
            Adjustment_Type = type, Is_Reversed = reversed
        };

        private Task<OperatingExpenseSummaryDto> ExpensesAsync(string type = "Month", DateTime? anchor = null) =>
            CreateService().GetOperatingExpenses(new ReportPeriodDto { Period_Type = type, Anchor_Date = anchor ?? new DateTime(2024, 3, 15) });

        // ---- CalculatePayroll (AC1) ----

        [Fact]
        public async Task GetOperatingExpenses_Month_SalaryIsOneTwelfthOfTheAnnualSalaries()
        {
            _repository.Employees = new() { Employee(120000m), Employee(60000m) };

            var result = await ExpensesAsync("Month");

            Assert.Equal(15000m, result.Salary_Expense);
        }

        [Fact]
        public async Task GetOperatingExpenses_Quarter_SalaryIsThreeTwelfths()
        {
            _repository.Employees = new() { Employee(120000m), Employee(60000m) };

            var result = await ExpensesAsync("Quarter", new DateTime(2024, 5, 20));

            Assert.Equal(45000m, result.Salary_Expense);
        }

        [Fact]
        public async Task GetOperatingExpenses_YearToDate_SalaryUsesTheAnchorMonthAsMonthCount()
        {
            _repository.Employees = new() { Employee(120000m), Employee(60000m) };

            var result = await ExpensesAsync("YearToDate", new DateTime(2024, 5, 10));

            Assert.Equal(75000m, result.Salary_Expense);
        }

        [Fact]
        public async Task GetOperatingExpenses_YearToDateInDecember_IsTheFullAnnualSalary()
        {
            _repository.Employees = new() { Employee(120000m) };

            var result = await ExpensesAsync("YearToDate", new DateTime(2024, 12, 31));

            Assert.Equal(120000m, result.Salary_Expense);
        }

        [Fact]
        public async Task GetOperatingExpenses_YearToDateInJanuary_IsOneMonth()
        {
            _repository.Employees = new() { Employee(120000m) };

            var result = await ExpensesAsync("YearToDate", new DateTime(2024, 1, 31));

            Assert.Equal(10000m, result.Salary_Expense);
        }

        [Fact]
        public async Task GetOperatingExpenses_SalaryNotDivisibleBy12_DoesNotLosePrecisionOverAFullQuarter()
        {
            _repository.Employees = new() { Employee(100000m) };

            var result = await ExpensesAsync("Quarter", new DateTime(2024, 5, 20));

            Assert.Equal(25000m, result.Salary_Expense);
        }

        [Fact]
        public async Task GetOperatingExpenses_Month_BonusIsOneTwelfthOfTheAnnualBonuses()
        {
            _repository.Employees = new() { Employee(0m, bonus: 12000m), Employee(0m, bonus: 6000m) };

            var result = await ExpensesAsync("Month");

            Assert.Equal(1500m, result.Bonus_Expense);
        }

        [Fact]
        public async Task GetOperatingExpenses_Quarter_BonusIsThreeTwelfths()
        {
            _repository.Employees = new() { Employee(0m, bonus: 12000m) };

            var result = await ExpensesAsync("Quarter", new DateTime(2024, 5, 20));

            Assert.Equal(3000m, result.Bonus_Expense);
        }

        [Fact]
        public async Task GetOperatingExpenses_CommissionIsSalesTimesRateOverOneHundred()
        {
            _repository.Employees = new() { Employee(0m, rate: 5m, sales: 50000m), Employee(0m, rate: 2.5m, sales: 20000m) };

            var result = await ExpensesAsync("Month");

            // 50 000 x 5 / 100 + 20 000 x 2.5 / 100 = 2 500 + 500
            Assert.Equal(3000m, result.Commission_Expense);
        }

        [Fact]
        public async Task GetOperatingExpenses_CommissionIsNotProratedByMonths()
        {
            _repository.Employees = new() { Employee(0m, rate: 5m, sales: 50000m) };

            var month = await ExpensesAsync("Month");
            var quarter = await ExpensesAsync("Quarter", new DateTime(2024, 3, 15));

            Assert.Equal(2500m, month.Commission_Expense);
            Assert.Equal(2500m, quarter.Commission_Expense);
        }

        [Fact]
        public async Task GetOperatingExpenses_NoSalesOrZeroRate_CommissionIsZero()
        {
            _repository.Employees = new() { Employee(0m, rate: 5m, sales: 0m), Employee(0m, rate: 0m, sales: 90000m) };

            var result = await ExpensesAsync("Month");

            Assert.Equal(0m, result.Commission_Expense);
        }

        [Fact]
        public async Task GetOperatingExpenses_PayrollPartsAreIndependentOfEachOther()
        {
            _repository.Employees = new() { Employee(120000m, bonus: 24000m, rate: 10m, sales: 1000m) };

            var result = await ExpensesAsync("Month");

            Assert.Equal(10000m, result.Salary_Expense);
            Assert.Equal(2000m, result.Bonus_Expense);
            Assert.Equal(100m, result.Commission_Expense);
        }

        [Fact]
        public async Task GetOperatingExpenses_NoEmployees_PayrollIsZero()
        {
            var result = await ExpensesAsync("Month");

            Assert.Equal(0m, result.Salary_Expense);
            Assert.Equal(0m, result.Bonus_Expense);
            Assert.Equal(0m, result.Commission_Expense);
        }

        // ---- IsOperatingExpense: payments (AC2, AC3, AC4) ----

        [Fact]
        public async Task GetOperatingExpenses_CategorizedPayment_IsCounted()
        {
            _repository.Candidates = new() { Payment(500m, "Office Expense") };

            var result = await ExpensesAsync();

            Assert.Equal(500m, result.Categorized_Payments);
            Assert.Equal(0m, result.Bank_Charges);
        }

        [Fact]
        public async Task GetOperatingExpenses_SeveralPayments_AreSummed()
        {
            _repository.Candidates = new() { Payment(500m, "Office Expense"), Payment(250.75m, "Utilities"), Payment(99.25m, "Rent") };

            var result = await ExpensesAsync();

            Assert.Equal(850m, result.Categorized_Payments);
        }

        [Fact]
        public async Task GetOperatingExpenses_PaymentWithoutACategoryOrLink_IsCounted()
        {
            _repository.Candidates = new() { Payment(100m, category: null) };

            var result = await ExpensesAsync();

            Assert.Equal(100m, result.Categorized_Payments);
        }

        [Theory]
        [InlineData("Payroll")]
        [InlineData("Tax Payment")]
        [InlineData("Loan Payment")]
        [InlineData("Customer Refund")]
        [InlineData("Transfer")]
        public async Task GetOperatingExpenses_ExcludedPaymentCategory_IsNotCounted(string category)
        {
            _repository.Candidates = new() { Payment(38450m, category) };

            var result = await ExpensesAsync();

            Assert.Equal(0m, result.Categorized_Payments);
            Assert.Equal(0m, result.Total_Operating_Expenses);
        }

        [Fact]
        public async Task GetOperatingExpenses_PaymentTiedToAnApBill_IsNotCounted()
        {
            _repository.Candidates = new() { Payment(8000m, "Office Expense", apBillId: 5) };

            var result = await ExpensesAsync();

            Assert.Equal(0m, result.Categorized_Payments);
        }

        [Fact]
        public async Task GetOperatingExpenses_PaymentTiedToAVehicle_IsNotCounted()
        {
            _repository.Candidates = new() { Payment(24000m, "Office Expense", vehicleId: 9) };

            var result = await ExpensesAsync();

            Assert.Equal(0m, result.Categorized_Payments);
        }

        [Fact]
        public async Task GetOperatingExpenses_ReversedPayment_IsNotCounted()
        {
            _repository.Candidates = new() { Payment(500m, "Office Expense", reversed: true), Payment(100m, "Office Expense") };

            var result = await ExpensesAsync();

            Assert.Equal(100m, result.Categorized_Payments);
        }

        [Fact]
        public async Task GetOperatingExpenses_CategoryMatchIsCaseSensitive()
        {
            _repository.Candidates = new() { Payment(300m, "payroll") };

            var result = await ExpensesAsync();

            Assert.Equal(300m, result.Categorized_Payments);
        }

        // ---- IsOperatingExpense: adjustments (AC2, AC5) ----

        [Fact]
        public async Task GetOperatingExpenses_BankServiceCharge_IsABankCharge()
        {
            _repository.Candidates = new() { Adjustment(125m, "Bank Service Charge") };

            var result = await ExpensesAsync();

            Assert.Equal(125m, result.Bank_Charges);
            Assert.Equal(0m, result.Categorized_Payments);
        }

        [Fact]
        public async Task GetOperatingExpenses_CardProcessingFee_IsABankCharge()
        {
            _repository.Candidates = new() { Adjustment(40m, "Card Processing Fee") };

            var result = await ExpensesAsync();

            Assert.Equal(40m, result.Bank_Charges);
        }

        [Fact]
        public async Task GetOperatingExpenses_InterestReceived_IsNeverAnExpense()
        {
            _repository.Candidates = new() { Adjustment(625m, "Interest Received") };

            var result = await ExpensesAsync();

            Assert.Equal(0m, result.Bank_Charges);
            Assert.Equal(0m, result.Total_Operating_Expenses);
        }

        [Fact]
        public async Task GetOperatingExpenses_OtherAdjustmentTypes_AreNotCounted()
        {
            _repository.Candidates = new() { Adjustment(50m, "Other Adjustment"), Adjustment(60m, "Vendor Refund") };

            var result = await ExpensesAsync();

            Assert.Equal(0m, result.Bank_Charges);
        }

        [Fact]
        public async Task GetOperatingExpenses_ReversedBankCharge_IsNotCounted()
        {
            _repository.Candidates = new() { Adjustment(125m, "Bank Service Charge", reversed: true), Adjustment(10m, "Bank Service Charge") };

            var result = await ExpensesAsync();

            Assert.Equal(10m, result.Bank_Charges);
        }

        [Fact]
        public async Task GetOperatingExpenses_UnknownSourceType_IsNotCounted()
        {
            _repository.Candidates = new()
            {
                new ExpenseCandidateDto { Source_Type = "Transfer", Source_Id = 3, Amount = 900m, Payment_Category = "Office Expense" }
            };

            var result = await ExpensesAsync();

            Assert.Equal(0m, result.Total_Operating_Expenses);
        }

        // ---- Total_Operating_Expenses ----

        [Fact]
        public async Task GetOperatingExpenses_StoryExample_OfficeExpense500AndBankCharge125Give625()
        {
            _repository.Candidates = new()
            {
                Payment(500m, "Office Expense"),
                Adjustment(125m, "Bank Service Charge"),
                Payment(38450m, "Payroll")
            };

            var result = await ExpensesAsync();

            Assert.Equal(625m, result.Categorized_Payments + result.Bank_Charges);
            Assert.Equal(625m, result.Total_Operating_Expenses);
        }

        [Fact]
        public async Task GetOperatingExpenses_TotalAddsAllFiveParts()
        {
            _repository.Employees = new() { Employee(120000m, bonus: 24000m, rate: 10m, sales: 1000m) };
            _repository.Candidates = new() { Payment(500m, "Office Expense"), Adjustment(125m, "Bank Service Charge") };

            var result = await ExpensesAsync();

            // 10 000 + 2 000 + 100 + 500 + 125
            Assert.Equal(12725m, result.Total_Operating_Expenses);
        }

        [Fact]
        public async Task GetOperatingExpenses_NothingFound_AllAmountsZero()
        {
            var result = await ExpensesAsync();

            Assert.Equal(0m, result.Categorized_Payments);
            Assert.Equal(0m, result.Bank_Charges);
            Assert.Equal(0m, result.Total_Operating_Expenses);
        }

        // ---- Period handling (Story 26 ResolvePeriod, unchanged) ----

        [Fact]
        public async Task GetOperatingExpenses_Month_PassesResolvedPeriodToBothRepositoryMethodsAndSummary()
        {
            var result = await ExpensesAsync("Month", new DateTime(2024, 3, 15));

            var expected = (new DateTime(2024, 3, 1), new DateTime(2024, 3, 31));
            Assert.Equal(expected, Assert.Single(_repository.PayrollCalls));
            Assert.Equal(expected, Assert.Single(_repository.CandidateCalls));
            Assert.Equal("Month", result.Period_Type);
            Assert.Equal(new DateTime(2024, 3, 1), result.Start_Date);
            Assert.Equal(new DateTime(2024, 3, 31), result.End_Date);
        }

        [Fact]
        public async Task GetOperatingExpenses_Quarter_UsesTheCalendarQuarter()
        {
            var result = await ExpensesAsync("Quarter", new DateTime(2024, 5, 20));

            Assert.Equal(new DateTime(2024, 4, 1), result.Start_Date);
            Assert.Equal(new DateTime(2024, 6, 30), result.End_Date);
        }

        [Fact]
        public async Task GetOperatingExpenses_YearToDate_RunsFromJanuaryFirstToAnchor()
        {
            await ExpensesAsync("YearToDate", new DateTime(2024, 3, 15));

            Assert.Equal((new DateTime(2024, 1, 1), new DateTime(2024, 3, 15)), Assert.Single(_repository.CandidateCalls));
        }

        [Theory]
        [InlineData("")]
        [InlineData("Week")]
        [InlineData("month")]
        public async Task GetOperatingExpenses_UnknownPeriodType_ThrowsAndDoesNotQuery(string type)
        {
            await Assert.ThrowsAsync<ValidationException>(() => ExpensesAsync(type));
            Assert.Empty(_repository.PayrollCalls);
            Assert.Empty(_repository.CandidateCalls);
        }

        [Fact]
        public async Task GetOperatingExpenses_MissingAnchorDate_ThrowsValidationException()
        {
            await Assert.ThrowsAsync<ValidationException>(() =>
                CreateService().GetOperatingExpenses(new ReportPeriodDto { Period_Type = "Month", Anchor_Date = null }));
            Assert.Empty(_repository.PayrollCalls);
        }
    }
}
