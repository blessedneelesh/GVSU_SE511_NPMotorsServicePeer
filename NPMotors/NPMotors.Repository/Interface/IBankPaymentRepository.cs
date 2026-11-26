using System.Data;
using NPMotors.Repository.Models;
using NPMotors.Repository.Models.Dto;

namespace NPMotors.Repository.Interface
{
    public interface IBankPaymentRepository
    {
        Task<long> InsertBankPayment(BankPayment payment, IDbTransaction tx);
        Task<IEnumerable<BankPayment>> GetPaymentsByBillId(long apBillId);
        // One database transaction: Out transaction, statement line, bank payment and book balance; all or none.
        Task<BankPaymentResponseDto> RecordMiscellaneousPayment(MiscellaneousPaymentCreateDto payment);
    }
}
