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

        // Vehicle + Service + Fee + Interest + Other - Discounts.
        private static decimal CalculateTotalRevenue(RevenueSummaryDto summary)
        {
            return summary.Vehicle_Revenue + summary.Service_Revenue + summary.Fee_Income
                 + summary.Interest_Income + summary.Other_Income - summary.Discounts;
        }
    }
}
