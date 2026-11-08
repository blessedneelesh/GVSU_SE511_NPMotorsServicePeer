using System.Data;
using NPMotors.Repository.Models.Dto;

namespace NPMotors.Services.Abstractions.ServiceInterfaces
{
    public interface ITransactionPostingService
    {
        Task<long> PostTransaction(PostTransactionRequest request, IDbTransaction? tx = null);
    }
}
