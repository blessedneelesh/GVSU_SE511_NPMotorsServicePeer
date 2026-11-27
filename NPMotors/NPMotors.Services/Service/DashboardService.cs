using NPMotors.Repository.Interface;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.RequestFeatures;
using NPMotors.Services.Abstractions.ServiceInterfaces;

namespace NPMotors.Services.Service
{
    public class DashboardService : IDashboardService
    {
        private readonly IDashboardRepository _dashboardRepository;
        private readonly IReportService _reportService;
        private readonly ISalesInvoiceService _salesInvoiceService;

        private const string MonthPeriod = "Month";
        private const int MonthlyRevenueWindow = 7;
        private const int RecentInvoiceCount = 4;

        public DashboardService(IDashboardRepository dashboardRepository, IReportService reportService, ISalesInvoiceService salesInvoiceService)
        {
            _dashboardRepository = dashboardRepository;
            _reportService = reportService;
            _salesInvoiceService = salesInvoiceService;
        }

        public async Task<DashboardSummaryDto> GetSummary(DateTime? asOfDate)
        {
            var asOf = (asOfDate ?? DateTime.Today).Date;
            var periodStart = new DateTime(asOf.Year, asOf.Month, 1);
            var periodEnd = periodStart.AddMonths(1).AddDays(-1);

            // ComparePeriods (Month) gives this month, last month and the change %, the same as the Profit & Loss page.
            var comparison = await _reportService.ComparePeriods(
                new ComparePeriodsDto { Period_Type = MonthPeriod, Current_Anchor_Date = asOf }, string.Empty);

            var inventory = await _dashboardRepository.GetInventoryCounts();
            var availableByMake = await _dashboardRepository.GetAvailableByMake();
            var receivables = await _dashboardRepository.GetReceivableTotals();
            var activeCustomers = await _dashboardRepository.GetActiveCustomerCount();
            var components = await _dashboardRepository.GetMonthlyRevenueComponents(
                periodStart.AddMonths(-(MonthlyRevenueWindow - 1)), periodEnd);
            var recent = await _salesInvoiceService.GetAllSalesInvoices(
                new SalesInvoiceParameters { Lifecycle = "posted", PageNumber = 1, PageSize = RecentInvoiceCount });

            return new DashboardSummaryDto
            {
                As_Of_Date = DateOnly.FromDateTime(asOf),
                Period_Start = DateOnly.FromDateTime(periodStart),
                Period_End = DateOnly.FromDateTime(periodEnd),
                Total_Inventory = inventory.Total_Inventory,
                Available_Vehicles = inventory.Available_Vehicles,
                Available_By_Make = availableByMake,
                Vehicles_Sold = comparison.Current.Cogs.Vehicles_Sold_Count,
                Vehicles_Sold_Previous = comparison.Previous.Cogs.Vehicles_Sold_Count,
                Revenue = comparison.Current.Total_Revenue,
                Revenue_Previous = comparison.Previous.Total_Revenue,
                Revenue_Change_Pct = comparison.Revenue_Change_Pct,
                Receivable_Balance = receivables.Receivable_Balance,
                Open_Receivables = receivables.Open_Receivables,
                Active_Customers = activeCustomers,
                Monthly_Revenue = BuildMonthlyRevenue(components),
                Recent_Invoices = recent.ToList()
            };
        }

        // One entry per month, oldest first. Revenue = Vehicle + Fees + Interest + Other - Discounts
        // (Service revenue is 0), the same total as ReportService.GetRevenue.
        private static List<MonthlyRevenueDto> BuildMonthlyRevenue(List<MonthlyRevenueComponentsDto> components)
        {
            return components
                .OrderBy(c => c.Month)
                .Select(c => new MonthlyRevenueDto
                {
                    Month = c.Month.ToString("yyyy-MM"),
                    Revenue = c.Vehicle_Revenue + c.Fee_Income + c.Interest_Income + c.Other_Income - c.Discounts
                })
                .ToList();
        }
    }
}
