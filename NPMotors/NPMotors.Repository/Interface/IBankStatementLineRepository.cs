using System.Data;
using NPMotors.Repository.Models;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.RequestFeatures;

namespace NPMotors.Repository.Interface
{
    public interface IBankStatementLineRepository
    {
        Task<decimal?> GetLastRunningBalance(long bankAccountId, IDbTransaction tx);
        Task<long> InsertStatementLine(BankStatementLine line, IDbTransaction tx);
        Task<PagedList<StatementLineResponseDto>> GetStatementLines(StatementLineParameters parameters);
    }
}
