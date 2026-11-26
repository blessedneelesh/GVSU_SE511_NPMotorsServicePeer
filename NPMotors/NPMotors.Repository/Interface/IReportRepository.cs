using NPMotors.Repository.Models.Dto;

namespace NPMotors.Repository.Interface
{
    public interface IReportRepository
    {
        Task<SalesRevenueTotalsDto> GetSalesRevenue(DateTime start, DateTime end);
        Task<List<IncomeAdjustmentDto>> GetIncomeAdjustments(DateTime start, DateTime end);
    }
}
