using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.RequestFeatures;

namespace NPMotors.Services.Abstractions.ServiceInterfaces
{
    public interface IStatementLineService
    {
        Task<PagedList<StatementLineResponseDto>> GetStatementLines(StatementLineParameters parameters);
    }
}
