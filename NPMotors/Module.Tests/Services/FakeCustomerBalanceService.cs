using System.Data;
using NPMotors.Services.Abstractions.ServiceInterfaces;

namespace Module.Tests.Services
{
    // Records UpdateCustomerTotals calls (and into an optional shared log so call order can be checked).
    public class FakeCustomerBalanceService : ICustomerBalanceService
    {
        private readonly List<string>? _log;
        public FakeCustomerBalanceService(List<string>? log = null) { _log = log; }

        public Exception? Throw { get; set; }
        public List<(long CustomerId, IDbTransaction Tx)> Calls { get; } = new();

        public Task UpdateCustomerTotals(long customerId, IDbTransaction tx)
        {
            _log?.Add("update-customer-totals");
            Calls.Add((customerId, tx));
            if (Throw is not null)
                throw Throw;
            return Task.CompletedTask;
        }
    }
}
