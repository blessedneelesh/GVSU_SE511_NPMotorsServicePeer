using NPMotors.Repository.Models.Dto;

namespace NPMotors.Repository.Interface
{
    public interface IReportRepository
    {
        Task<SalesRevenueTotalsDto> GetSalesRevenue(DateTime start, DateTime end);
        Task<List<IncomeAdjustmentDto>> GetIncomeAdjustments(DateTime start, DateTime end);
        Task<List<SoldVehicleCostDto>> GetSoldVehicleCosts(DateTime start, DateTime end);
        Task<List<EmployeePayrollDto>> GetEmployeePayroll(DateTime start, DateTime end);
        Task<List<ExpenseCandidateDto>> GetExpenseCandidates(DateTime start, DateTime end);
    }
}
