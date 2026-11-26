using System.Data;
using NPMotors.Repository.Models;

namespace NPMotors.Repository.Interface
{
    public interface IBankPaymentRepository
    {
        Task<long> InsertBankPayment(BankPayment payment, IDbTransaction tx);
        Task<IEnumerable<BankPayment>> GetPaymentsByBillId(long apBillId);
    }
}
