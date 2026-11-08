using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.RequestFeatures;

namespace NPMotors.Services.Abstractions.ServiceInterfaces
{
    public interface ITransactionService
    {
        Task<PagedList<TransactionResponseDto>> GetAllTransactions(TransactionParameters parameters);
        Task<TransactionResponseDto?> GetTransactionById(long id);
    }
}
