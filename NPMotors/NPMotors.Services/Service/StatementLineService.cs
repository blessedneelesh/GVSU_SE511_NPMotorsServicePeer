using NPMotors.Repository.Interface;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.RequestFeatures;
using NPMotors.Services.Abstractions.ServiceInterfaces;

namespace NPMotors.Services.Service
{
    public class StatementLineService : IStatementLineService
    {
        private readonly IBankStatementLineRepository _bankStatementLineRepository;

        public StatementLineService(IBankStatementLineRepository bankStatementLineRepository)
        {
            _bankStatementLineRepository = bankStatementLineRepository;
        }

        public Task<PagedList<StatementLineResponseDto>> GetStatementLines(StatementLineParameters parameters)
        {
            return _bankStatementLineRepository.GetStatementLines(parameters);
        }
    }
}
