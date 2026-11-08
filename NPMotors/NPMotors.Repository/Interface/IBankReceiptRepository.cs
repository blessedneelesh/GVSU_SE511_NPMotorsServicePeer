using System.Data;
using NPMotors.Repository.Models;

namespace NPMotors.Repository.Interface
{
    public interface IBankReceiptRepository
    {
        Task<long> InsertBankReceipt(BankReceipt receipt, IDbTransaction tx);
    }
}
