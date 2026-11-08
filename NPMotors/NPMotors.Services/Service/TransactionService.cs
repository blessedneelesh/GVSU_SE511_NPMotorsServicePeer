using NPMotors.Repository.Interface;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.RequestFeatures;
using NPMotors.Services.Abstractions.ServiceInterfaces;

namespace NPMotors.Services.Service
{
    public class TransactionService : ITransactionService
    {
        private readonly ITransactionRepository _transactionRepository;

        public TransactionService(ITransactionRepository transactionRepository)
        {
            _transactionRepository = transactionRepository;
        }

        public Task<PagedList<TransactionResponseDto>> GetAllTransactions(TransactionParameters parameters)
        {
            return _transactionRepository.GetAllTransactions(parameters);
        }

        public Task<TransactionResponseDto?> GetTransactionById(long id)
        {
            return _transactionRepository.GetTransactionById(id);
        }
    }
}
