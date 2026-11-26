using NPMotors.Repository.Models.Dto;

namespace NPMotors.Services.Abstractions.ServiceInterfaces
{
    public interface IReportService
    {
        Task<RevenueSummaryDto> GetRevenue(ReportPeriodDto period);
    }
}
