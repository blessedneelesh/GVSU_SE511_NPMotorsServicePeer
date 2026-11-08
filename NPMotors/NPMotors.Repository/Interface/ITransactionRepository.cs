using System.Data;
using NPMotors.Repository.Models;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.RequestFeatures;

namespace NPMotors.Repository.Interface
{
    public interface ITransactionRepository
    {
        Task<long> InsertTransaction(Transaction transaction, IDbTransaction tx);
        Task<PagedList<TransactionResponseDto>> GetAllTransactions(TransactionParameters parameters);
        Task<TransactionResponseDto?> GetTransactionById(long id);
    }
}
