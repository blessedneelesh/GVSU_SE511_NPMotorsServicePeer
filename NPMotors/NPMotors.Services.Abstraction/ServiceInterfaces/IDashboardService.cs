using NPMotors.Repository.Models.Dto;

namespace NPMotors.Services.Abstractions.ServiceInterfaces
{
    public interface IDashboardService
    {
        Task<DashboardSummaryDto> GetSummary(DateTime? asOfDate);
    }
}
