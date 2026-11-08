using NPMotors.Repository.Interface;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.RequestFeatures;
using NPMotors.Services.Abstractions.ServiceInterfaces;

namespace NPMotors.Services.Service
{
    public class ArService : IArService
    {
        private readonly IArInvoiceRepository _arInvoiceRepository;

        private const string OverdueStatus = "Overdue";

        public ArService(IArInvoiceRepository arInvoiceRepository)
        {
            _arInvoiceRepository = arInvoiceRepository;
        }

        public async Task<PagedList<ArAgingDto>> GetArAging(ArAgingParameters parameters)
        {
            parameters.AsOfDate ??= DateTime.Today;
            var asOfDate = parameters.AsOfDate.Value;

            var items = await _arInvoiceRepository.GetArAging(parameters);
            foreach (var item in items)
            {
                item.Days_Overdue = CalculateDaysOverdue(item.Due_Date, asOfDate);
                item.Aging_Status = DetermineAgingStatus(item.Balance, item.Status, item.Days_Overdue);
            }
            return items;
        }

        // max(0, (asOfDate - dueDate).Days); 0 when dueDate is null.
        private static int CalculateDaysOverdue(DateTime? dueDate, DateTime asOfDate)
        {
            if (dueDate is null)
                return 0;
            return Math.Max(0, (asOfDate.Date - dueDate.Value.Date).Days);
        }

        // "Overdue" when balance > 0 and daysOverdue > 0, otherwise the stored status.
        private static string DetermineAgingStatus(decimal balance, string status, int daysOverdue)
        {
            return balance > 0 && daysOverdue > 0 ? OverdueStatus : status;
        }
    }
}
