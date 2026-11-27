using System.ComponentModel.DataAnnotations;
using NPMotors.Repository.Interface;
using NPMotors.Repository.Models.Dto;
using NPMotors.Services.Abstractions.ServiceInterfaces;

namespace NPMotors.Services.Service
{
    public class ReportService : IReportService
    {
        private readonly IReportRepository _reportRepository;

        private const string InterestType = "Interest Received";
        private const string OtherType = "Other Adjustment";

        private const string PaymentSource = "Payment";
        private const string AdjustmentSource = "Adjustment";
        private const string BankServiceCharge = "Bank Service Charge";
        private const string CardProcessingFee = "Card Processing Fee";
        private static readonly string[] ExcludedPaymentCategories =
            { "Payroll", "Tax Payment", "Loan Payment", "Customer Refund", "Transfer" };

        public ReportService(IReportRepository reportRepository)
        {
            _reportRepository = reportRepository;
        }

        public async Task<RevenueSummaryDto> GetRevenue(ReportPeriodDto period)
        {
            var (start, end) = ResolvePeriod(period);
            var sales = await _reportRepository.GetSalesRevenue(start, end);
            var adjustments = await _reportRepository.GetIncomeAdjustments(start, end);
            var (interest, other) = SplitAdjustmentIncome(adjustments);

            var summary = new RevenueSummaryDto
            {
                Period_Type = period.Period_Type,
                Start_Date = start,
                End_Date = end,
                Vehicle_Revenue = sales.Vehicle_Revenue,
                Service_Revenue = sales.Service_Revenue,
                Fee_Income = sales.Fee_Income,
                Discounts = sales.Discounts,
                Invoice_Count = sales.Invoice_Count,
                Interest_Income = interest,
                Other_Income = other
            };
            summary.Total_Revenue = CalculateTotalRevenue(summary);
            return summary;
        }

        public async Task<CogsSummaryDto> GetCostOfGoodsSold(ReportPeriodDto period)
        {
            var (start, end) = ResolvePeriod(period);
            var vehicles = await _reportRepository.GetSoldVehicleCosts(start, end);

            var summary = new CogsSummaryDto
            {
                Period_Type = period.Period_Type,
                Start_Date = start,
                End_Date = end,
                Vehicle_Cogs = CalculateVehicleCogs(vehicles),
                // No part sale or usage records exist yet, so parts COGS stays zero.
                Parts_Cogs = 0m,
                Vehicles_Sold_Count = vehicles.Select(v => v.Vehicle_Id).Distinct().Count()
            };
            summary.Total_Cogs = summary.Vehicle_Cogs + summary.Parts_Cogs;
            return summary;
        }

        public async Task<OperatingExpenseSummaryDto> GetOperatingExpenses(ReportPeriodDto period)
        {
            var (start, end) = ResolvePeriod(period);
            var months = period.Period_Type switch
            {
                "Month" => 1,
                "Quarter" => 3,
                _ => period.Anchor_Date!.Value.Month
            };

            var employees = await _reportRepository.GetEmployeePayroll(start, end);
            var (salary, bonus, commission) = CalculatePayroll(employees, months);
            var candidates = await _reportRepository.GetExpenseCandidates(start, end);
            var kept = candidates.Where(IsOperatingExpense).ToList();

            var summary = new OperatingExpenseSummaryDto
            {
                Period_Type = period.Period_Type,
                Start_Date = start,
                End_Date = end,
                Salary_Expense = salary,
                Bonus_Expense = bonus,
                Commission_Expense = commission,
                Categorized_Payments = kept.Where(c => c.Source_Type == PaymentSource).Sum(c => c.Amount),
                Bank_Charges = kept.Where(c => c.Source_Type == AdjustmentSource).Sum(c => c.Amount)
            };
            summary.Total_Operating_Expenses = summary.Salary_Expense + summary.Bonus_Expense
                + summary.Commission_Expense + summary.Categorized_Payments + summary.Bank_Charges;
            return summary;
        }

        public async Task<ProfitAndLossDto> GetProfitAndLoss(ReportPeriodDto period, string generatedBy)
        {
            // All three resolve the same period, so they use the same Start_Date and End_Date.
            var revenue = await GetRevenue(period);
            var cogs = await GetCostOfGoodsSold(period);
            var expenses = await GetOperatingExpenses(period);

            var report = new ProfitAndLossDto
            {
                Period_Type = period.Period_Type,
                Start_Date = revenue.Start_Date,
                End_Date = revenue.End_Date,
                Revenue = revenue,
                Cogs = cogs,
                Operating_Expenses = expenses,
                Total_Revenue = revenue.Total_Revenue,
                Total_Cogs = cogs.Total_Cogs,
                Total_Operating_Expenses = expenses.Total_Operating_Expenses
            };
            report.Gross_Profit = CalculateGrossProfit(report.Total_Revenue, report.Total_Cogs);
            report.Net_Profit = CalculateNetProfit(report.Gross_Profit, report.Total_Operating_Expenses);
            report.Gross_Margin = CalculateMargin(report.Gross_Profit, report.Total_Revenue);
            report.Net_Margin = CalculateMargin(report.Net_Profit, report.Total_Revenue);
            report.Generated_Date = DateTime.Now;
            report.Generated_By = generatedBy;
            return report;
        }

        public async Task<PeriodComparisonDto> ComparePeriods(ComparePeriodsDto comparison, string generatedBy)
        {
            if (comparison.Current_Anchor_Date is null)
                throw new ValidationException("Current_Anchor_Date is required.");

            var currentAnchor = comparison.Current_Anchor_Date.Value;
            var previousAnchor = comparison.Previous_Anchor_Date ?? comparison.Period_Type switch
            {
                "Month" => currentAnchor.AddMonths(-1),
                "Quarter" => currentAnchor.AddMonths(-3),
                "YearToDate" => currentAnchor.AddYears(-1),
                _ => throw new ValidationException("Period_Type must be Month, Quarter or YearToDate.")
            };

            // Both periods use the same Period_Type, so they follow the same rules.
            var current = new ReportPeriodDto { Period_Type = comparison.Period_Type, Anchor_Date = currentAnchor };
            var previous = new ReportPeriodDto { Period_Type = comparison.Period_Type, Anchor_Date = previousAnchor };
            var (currentStart, currentEnd) = ResolvePeriod(current);
            var (previousStart, previousEnd) = ResolvePeriod(previous);
            EnsureNoOverlap(currentStart, currentEnd, previousStart, previousEnd);

            var currentReport = await GetProfitAndLoss(current, generatedBy);
            var previousReport = await GetProfitAndLoss(previous, generatedBy);

            return new PeriodComparisonDto
            {
                Current = currentReport,
                Previous = previousReport,
                Revenue_Change_Pct = CalculatePercentageChange(currentReport.Total_Revenue, previousReport.Total_Revenue),
                Cogs_Change_Pct = CalculatePercentageChange(currentReport.Total_Cogs, previousReport.Total_Cogs),
                Gross_Profit_Change_Pct = CalculatePercentageChange(currentReport.Gross_Profit, previousReport.Gross_Profit),
                Operating_Expense_Change_Pct = CalculatePercentageChange(currentReport.Total_Operating_Expenses, previousReport.Total_Operating_Expenses),
                Net_Profit_Change_Pct = CalculatePercentageChange(currentReport.Net_Profit, previousReport.Net_Profit),
                Gross_Margin_Change = currentReport.Gross_Margin - previousReport.Gross_Margin,
                Net_Margin_Change = currentReport.Net_Margin - previousReport.Net_Margin
            };
        }

        // Month -> first to last day of the anchor month; Quarter -> its calendar quarter;
        // YearToDate -> 1 Jan to the anchor date.
        private static (DateTime Start, DateTime End) ResolvePeriod(ReportPeriodDto period)
        {
            if (period.Anchor_Date is null)
                throw new ValidationException("Anchor_Date is required.");

            var anchor = period.Anchor_Date.Value.Date;
            switch (period.Period_Type)
            {
                case "Month":
                    var monthStart = new DateTime(anchor.Year, anchor.Month, 1);
                    return (monthStart, monthStart.AddMonths(1).AddDays(-1));
                case "Quarter":
                    var quarterStart = new DateTime(anchor.Year, (anchor.Month - 1) / 3 * 3 + 1, 1);
                    return (quarterStart, quarterStart.AddMonths(3).AddDays(-1));
                case "YearToDate":
                    return (new DateTime(anchor.Year, 1, 1), anchor);
                default:
                    throw new ValidationException("Period_Type must be Month, Quarter or YearToDate.");
            }
        }

        // "Interest Received" -> Interest; "Other Adjustment" -> Other. Other types are ignored.
        private static (decimal Interest, decimal Other) SplitAdjustmentIncome(List<IncomeAdjustmentDto> adjustments)
        {
            decimal interest = 0m, other = 0m;
            foreach (var adjustment in adjustments)
            {
                if (adjustment.Adjustment_Type == InterestType)
                    interest += adjustment.Amount;
                else if (adjustment.Adjustment_Type == OtherType)
                    other += adjustment.Amount;
            }
            return (interest, other);
        }

        // Salary = sum of Salary / 12 x months; Bonus = sum of Bonus / 12 x months;
        // Commission = sum of Commission_Sales x Commission_Rate / 100. Multiplied before dividing to keep decimal precision.
        private static (decimal Salary, decimal Bonus, decimal Commission) CalculatePayroll(List<EmployeePayrollDto> employees, int months)
        {
            var salary = employees.Sum(e => e.Salary) * months / 12m;
            var bonus = employees.Sum(e => e.Bonus) * months / 12m;
            var commission = employees.Sum(e => e.Commission_Sales * e.Commission_Rate / 100m);
            return (salary, bonus, commission);
        }

        // False when reversed. "Payment": false for inventory purchases / AP settlements (Ap_Bill_Id or Vehicle_Id set)
        // and for the categories Payroll, Tax Payment, Loan Payment, Customer Refund and Transfer; otherwise true.
        // "Adjustment": true for Bank Service Charge and Card Processing Fee ("Interest Received" is income).
        private static bool IsOperatingExpense(ExpenseCandidateDto candidate)
        {
            if (candidate.Is_Reversed)
                return false;

            if (candidate.Source_Type == PaymentSource)
            {
                if (candidate.Ap_Bill_Id is not null || candidate.Vehicle_Id is not null)
                    return false;
                return !ExcludedPaymentCategories.Contains(candidate.Payment_Category);
            }

            if (candidate.Source_Type == AdjustmentSource)
                return candidate.Adjustment_Type == BankServiceCharge || candidate.Adjustment_Type == CardProcessingFee;

            return false;
        }

        // (current - previous) / |previous| x 100 rounded to 2 decimals; null ("unavailable") when previous is 0.
        // |previous| keeps the sign right for a negative profit: -1,000 -> 500 gives +150%.
        private static decimal? CalculatePercentageChange(decimal current, decimal previous)
        {
            if (previous == 0m)
                return null;
            return Math.Round((current - previous) / Math.Abs(previous) * 100m, 2, MidpointRounding.AwayFromZero);
        }

        // previousEnd must be before currentStart, otherwise the periods overlap (a business-rule violation, 400).
        private static void EnsureNoOverlap(DateTime currentStart, DateTime currentEnd, DateTime previousStart, DateTime previousEnd)
        {
            if (previousEnd >= currentStart)
                throw new ValidationException("The previous period must end before the current period starts.");
        }

        // totalRevenue - totalCogs.
        private static decimal CalculateGrossProfit(decimal totalRevenue, decimal totalCogs)
        {
            return totalRevenue - totalCogs;
        }

        // grossProfit - totalOperatingExpenses.
        private static decimal CalculateNetProfit(decimal grossProfit, decimal totalOperatingExpenses)
        {
            return grossProfit - totalOperatingExpenses;
        }

        // profit / totalRevenue x 100 rounded to 2 decimals; null when totalRevenue is 0. A negative profit gives a negative margin.
        private static decimal? CalculateMargin(decimal profit, decimal totalRevenue)
        {
            if (totalRevenue == 0m)
                return null;
            return Math.Round(profit / totalRevenue * 100m, 2, MidpointRounding.AwayFromZero);
        }

        // Sum of Total_Cost over the list, each Vehicle_Id counted once; an empty list gives 0.
        private static decimal CalculateVehicleCogs(List<SoldVehicleCostDto> vehicles)
        {
            return vehicles
                .GroupBy(v => v.Vehicle_Id)
                .Sum(g => g.First().Total_Cost);
        }

        // Vehicle + Service + Fee + Interest + Other - Discounts.
        private static decimal CalculateTotalRevenue(RevenueSummaryDto summary)
        {
            return summary.Vehicle_Revenue + summary.Service_Revenue + summary.Fee_Income
                 + summary.Interest_Income + summary.Other_Income - summary.Discounts;
        }
    }
}
