using System.Data;
using NPMotors.Repository.Interface;
using NPMotors.Repository.Models;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.RequestFeatures;
using NPMotors.Services.Service;

namespace Module.Tests.Services
{
    // Story 17, GetArAging. CalculateDaysOverdue and DetermineAgingStatus are private static;
    // they are tested only through GetArAging. As-of date used below: 2026-11-08.
    public class ArServiceTest
    {
        private static readonly DateTime AsOf = new(2026, 11, 8);

        private class FakeArInvoiceRepository : IArInvoiceRepository
        {
            public PagedList<ArAgingDto> Page { get; set; } = new(new List<ArAgingDto>(), 0, 1, 5);
            public List<ArAgingParameters> Calls { get; } = new();

            public Task<int> CloseArInvoice(long invoiceId, System.Data.IDbTransaction tx) => throw new NotSupportedException();

            public Task<PagedList<ArAgingDto>> GetArAging(ArAgingParameters parameters)
            {
                Calls.Add(parameters);
                return Task.FromResult(Page);
            }

            public Task<long> InsertArInvoice(ArInvoice arInvoice, IDbTransaction tx) => throw new NotSupportedException();
            public Task<ArInvoice?> GetArInvoiceById(long id) => throw new NotSupportedException();
            public Task<int> UpdateArPayment(ArInvoice ar, decimal expectedBalance, IDbTransaction tx) => throw new NotSupportedException();
        }

        private readonly FakeArInvoiceRepository _repository = new();

        private ArService CreateService() => new(_repository);

        private static ArAgingDto Item(DateTime? due, decimal balance = 500m, string status = "unpaid") => new()
        {
            Id = 1, Number = "AR-000001", CustomerId = 7, Customer_Name = "Ann Lee", Invoice_Number = "INV-1",
            Due_Date = due, Amount = 1000m, Paid = 1000m - balance, Balance = balance, Status = status
        };

        private async Task<ArAgingDto> AgeSingleAsync(DateTime? due, decimal balance = 500m, string status = "unpaid", DateTime? asOf = null)
        {
            _repository.Page = new PagedList<ArAgingDto>(new List<ArAgingDto> { Item(due, balance, status) }, 1, 1, 5);
            var result = await CreateService().GetArAging(new ArAgingParameters { AsOfDate = asOf ?? AsOf });
            return Assert.Single(result);
        }

        // ---- CalculateDaysOverdue (AC 2) ----

        [Fact]
        public async Task GetArAging_DueTenDaysBeforeAsOf_DaysOverdueIs10()
        {
            var item = await AgeSingleAsync(new DateTime(2026, 10, 29));

            Assert.Equal(10, item.Days_Overdue);
        }

        [Fact]
        public async Task GetArAging_DueOneDayBeforeAsOf_DaysOverdueIs1()
        {
            var item = await AgeSingleAsync(new DateTime(2026, 11, 7));

            Assert.Equal(1, item.Days_Overdue);
        }

        [Fact]
        public async Task GetArAging_DueOnAsOfDate_DaysOverdueIsZero()
        {
            var item = await AgeSingleAsync(new DateTime(2026, 11, 8));

            Assert.Equal(0, item.Days_Overdue);
        }

        [Fact]
        public async Task GetArAging_DueAfterAsOf_DaysOverdueNeverBelowZero()
        {
            var item = await AgeSingleAsync(new DateTime(2026, 11, 18));

            Assert.Equal(0, item.Days_Overdue);
        }

        [Fact]
        public async Task GetArAging_NoDueDate_DaysOverdueIsZero()
        {
            var item = await AgeSingleAsync(null);

            Assert.Equal(0, item.Days_Overdue);
        }

        [Fact]
        public async Task GetArAging_TimeOfDayIsIgnored()
        {
            var item = await AgeSingleAsync(new DateTime(2026, 11, 7, 23, 59, 0), asOf: new DateTime(2026, 11, 8, 0, 1, 0));

            Assert.Equal(1, item.Days_Overdue);
        }

        [Fact]
        public async Task GetArAging_DaysOverdueUsesTheGivenAsOfDate()
        {
            // Due 2026-10-29: 10 days at 2026-11-08, 20 days at 2026-11-18.
            var item = await AgeSingleAsync(new DateTime(2026, 10, 29), asOf: new DateTime(2026, 11, 18));

            Assert.Equal(20, item.Days_Overdue);
        }

        [Fact]
        public async Task GetArAging_NoAsOfDate_DefaultsToToday()
        {
            var parameters = new ArAgingParameters();
            var before = DateTime.Today;
            _repository.Page = new PagedList<ArAgingDto>(new List<ArAgingDto> { Item(before.AddDays(-3)) }, 1, 1, 5);

            var result = await CreateService().GetArAging(parameters);

            Assert.Equal(3, Assert.Single(result).Days_Overdue);
            Assert.True(parameters.AsOfDate == before || parameters.AsOfDate == DateTime.Today);
        }

        // ---- DetermineAgingStatus (AC 3) ----

        [Fact]
        public async Task GetArAging_OpenBalanceAndPastDue_AgingStatusIsOverdue()
        {
            var item = await AgeSingleAsync(new DateTime(2026, 11, 7), 500m, "unpaid");

            Assert.Equal("Overdue", item.Aging_Status);
        }

        [Fact]
        public async Task GetArAging_PartialAndPastDue_AgingStatusIsOverdue()
        {
            var item = await AgeSingleAsync(new DateTime(2026, 10, 1), 200m, "partial");

            Assert.Equal("Overdue", item.Aging_Status);
        }

        [Fact]
        public async Task GetArAging_OverdueKeepsTheStoredStatusInStatus()
        {
            var item = await AgeSingleAsync(new DateTime(2026, 10, 1), 200m, "partial");

            Assert.Equal("partial", item.Status);
        }

        [Fact]
        public async Task GetArAging_UnpaidNotYetDue_AgingStatusIsStoredStatus()
        {
            var item = await AgeSingleAsync(new DateTime(2026, 11, 18), 500m, "unpaid");

            Assert.Equal("unpaid", item.Aging_Status);
        }

        [Fact]
        public async Task GetArAging_PartialNotYetDue_AgingStatusIsStoredStatus()
        {
            var item = await AgeSingleAsync(new DateTime(2026, 11, 18), 500m, "partial");

            Assert.Equal("partial", item.Aging_Status);
        }

        [Fact]
        public async Task GetArAging_DueToday_IsNotOverdue()
        {
            var item = await AgeSingleAsync(new DateTime(2026, 11, 8), 500m, "unpaid");

            Assert.Equal("unpaid", item.Aging_Status);
        }

        [Fact]
        public async Task GetArAging_NoDueDate_IsNotOverdue()
        {
            var item = await AgeSingleAsync(null, 500m, "partial");

            Assert.Equal("partial", item.Aging_Status);
        }

        [Fact]
        public async Task GetArAging_ZeroBalancePastDue_IsNotOverdue()
        {
            var item = await AgeSingleAsync(new DateTime(2026, 10, 1), 0m, "paid");

            Assert.Equal(0m, item.Balance);
            Assert.Equal("paid", item.Aging_Status);
        }

        // ---- GetArAging as a whole ----

        [Fact]
        public async Task GetArAging_AppliesBothHelpersToEveryItem()
        {
            _repository.Page = new PagedList<ArAgingDto>(new List<ArAgingDto>
            {
                Item(new DateTime(2026, 10, 29), 500m, "unpaid"),    // 10 days, Overdue
                Item(new DateTime(2026, 11, 8), 300m, "partial"),    // 0 days, partial
                Item(new DateTime(2026, 11, 28), 100m, "unpaid"),    // 0 days, unpaid
                Item(new DateTime(2026, 11, 6), 50m, "partial")      // 2 days, Overdue
            }, 4, 1, 5);

            var result = await CreateService().GetArAging(new ArAgingParameters { AsOfDate = AsOf });

            Assert.Equal(new[] { 10, 0, 0, 2 }, result.Select(i => i.Days_Overdue).ToArray());
            Assert.Equal(new[] { "Overdue", "partial", "unpaid", "Overdue" }, result.Select(i => i.Aging_Status).ToArray());
        }

        [Fact]
        public async Task GetArAging_KeepsTheStoredColumnsUnchanged()
        {
            var item = await AgeSingleAsync(new DateTime(2026, 10, 29), 500m, "unpaid");

            Assert.Equal(1, item.Id);
            Assert.Equal("AR-000001", item.Number);
            Assert.Equal(7, item.CustomerId);
            Assert.Equal("Ann Lee", item.Customer_Name);
            Assert.Equal("INV-1", item.Invoice_Number);
            Assert.Equal(new DateTime(2026, 10, 29), item.Due_Date);
            Assert.Equal(1000m, item.Amount);
            Assert.Equal(500m, item.Paid);
            Assert.Equal(500m, item.Balance);
            Assert.Equal("unpaid", item.Status);
        }

        [Fact]
        public async Task GetArAging_KeepsMetaData()
        {
            _repository.Page = new PagedList<ArAgingDto>(new List<ArAgingDto> { Item(AsOf), Item(AsOf) }, 12, 2, 5);

            var result = await CreateService().GetArAging(new ArAgingParameters { AsOfDate = AsOf, PageNumber = 2 });

            Assert.Equal(12, result.MetaData.TotalCount);
            Assert.Equal(2, result.MetaData.CurrentPage);
            Assert.Equal(5, result.MetaData.PageSize);
            Assert.Equal(3, result.MetaData.TotalPages);
        }

        [Fact]
        public async Task GetArAging_EmptyPage_ReturnsEmptyListWithMetaData()
        {
            _repository.Page = new PagedList<ArAgingDto>(new List<ArAgingDto>(), 0, 1, 5);

            var result = await CreateService().GetArAging(new ArAgingParameters { AsOfDate = AsOf });

            Assert.Empty(result);
            Assert.Equal(0, result.MetaData.TotalCount);
            Assert.Equal(1, result.MetaData.CurrentPage);
        }

        [Fact]
        public async Task GetArAging_PassesTheParametersToTheRepositoryUnchanged()
        {
            var parameters = new ArAgingParameters { AsOfDate = AsOf, Status = "Overdue", CustomerId = 42, PageNumber = 3, PageSize = 20 };

            await CreateService().GetArAging(parameters);

            var call = Assert.Single(_repository.Calls);
            Assert.Same(parameters, call);
            Assert.Equal(AsOf, call.AsOfDate);
            Assert.Equal("Overdue", call.Status);
            Assert.Equal(42, call.CustomerId);
            Assert.Equal(3, call.PageNumber);
            Assert.Equal(20, call.PageSize);
        }

        [Fact]
        public async Task GetArAging_RepositoryIsCalledOnce()
        {
            await CreateService().GetArAging(new ArAgingParameters { AsOfDate = AsOf });

            Assert.Single(_repository.Calls);
        }
    }
}
