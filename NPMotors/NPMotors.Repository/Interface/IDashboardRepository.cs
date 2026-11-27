using NPMotors.Repository.Models.Dto;

namespace NPMotors.Repository.Interface
{
    public interface IDashboardRepository
    {
        Task<InventoryCountsDto> GetInventoryCounts();
        Task<List<MakeCountDto>> GetAvailableByMake();
        Task<ReceivableTotalsDto> GetReceivableTotals();
        Task<int> GetActiveCustomerCount();
        Task<List<MonthlyRevenueComponentsDto>> GetMonthlyRevenueComponents(DateTime start, DateTime end);
    }
}
