using System.Data;
using NPMotors.Repository.Interface;
using NPMotors.Services.Abstractions.ServiceInterfaces;

namespace NPMotors.Services.Service
{
    public class CustomerBalanceService : ICustomerBalanceService
    {
        private readonly ICustomerRepository _customerRepository;

        public CustomerBalanceService(ICustomerRepository customerRepository)
        {
            _customerRepository = customerRepository;
        }

        // Runs inside the caller's transaction, just before the caller commits.
        // Both totals are recalculated from the source rows, never added to or subtracted from.
        public async Task UpdateCustomerTotals(long customerId, IDbTransaction tx)
        {
            var openArBalances = await _customerRepository.GetOpenArBalances(customerId, tx);
            var postedInvoiceTotals = await _customerRepository.GetPostedInvoiceTotals(customerId, tx);

            var balance = RecalculateCustomerBalance(openArBalances);
            var totalPurchases = RecalculateTotalPurchases(postedInvoiceTotals);

            await _customerRepository.UpdateCustomerTotals(customerId, balance, totalPurchases, tx);
        }

        // AC 1 and AC 4: sum of open AR balances, never negative.
        private static decimal RecalculateCustomerBalance(IEnumerable<decimal> openArBalances)
        {
            return Math.Max(0, openArBalances.Sum());
        }

        // AC 2: sum of posted, non-cancelled invoice totals; 0 when none.
        private static decimal RecalculateTotalPurchases(IEnumerable<decimal> postedInvoiceTotals)
        {
            return postedInvoiceTotals.Sum();
        }
    }
}
