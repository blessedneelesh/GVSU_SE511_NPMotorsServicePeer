using System.Data;

namespace NPMotors.Services.Abstractions.ServiceInterfaces
{
    public interface ICustomerBalanceService
    {
        Task UpdateCustomerTotals(long customerId, IDbTransaction tx);
    }
}
