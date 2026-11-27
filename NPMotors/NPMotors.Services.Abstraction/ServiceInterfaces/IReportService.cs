using NPMotors.Repository.Models.Dto;

namespace NPMotors.Services.Abstractions.ServiceInterfaces
{
    public interface IReportService
    {
        Task<RevenueSummaryDto> GetRevenue(ReportPeriodDto period);
        Task<CogsSummaryDto> GetCostOfGoodsSold(ReportPeriodDto period);
        Task<OperatingExpenseSummaryDto> GetOperatingExpenses(ReportPeriodDto period);
        Task<ProfitAndLossDto> GetProfitAndLoss(ReportPeriodDto period, string generatedBy);
        Task<PeriodComparisonDto> ComparePeriods(ComparePeriodsDto comparison, string generatedBy);
    }
}
