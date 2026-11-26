using System.Data;
using NPMotors.Repository.Models;

namespace NPMotors.Repository.Interface
{
    public interface IAPBillRepository
    {
        // With a transaction the row is locked (FOR UPDATE) until it ends; null when not found.
        Task<APBill?> GetApBillById(long id, IDbTransaction? tx);
        Task<int> UpdateApBillPayment(APBill bill, IDbTransaction tx);
        Task<decimal> GetOpenBalanceByVendor(long vendorId, IDbTransaction tx);
    }
}
