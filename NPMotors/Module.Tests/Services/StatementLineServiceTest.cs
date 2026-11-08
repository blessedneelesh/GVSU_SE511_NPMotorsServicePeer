using System.Data;
using NPMotors.Repository.Interface;
using NPMotors.Repository.Models;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.RequestFeatures;
using NPMotors.Services.Service;

namespace Module.Tests.Services
{
    public class StatementLineServiceTest
    {
        // Hand-written fake: the test projects do not use a mocking library.
        private class FakeBankStatementLineRepository : IBankStatementLineRepository
        {
            public PagedList<StatementLineResponseDto> GetAllResult { get; set; } =
                new(new List<StatementLineResponseDto>(), 0, 1, 5);

            public List<StatementLineParameters> GetAllCalls { get; } = new();

            public Task<PagedList<StatementLineResponseDto>> GetStatementLines(StatementLineParameters parameters)
            {
                GetAllCalls.Add(parameters);
                return Task.FromResult(GetAllResult);
            }

            public Task<decimal?> GetLastRunningBalance(long bankAccountId, IDbTransaction tx) =>
                throw new NotSupportedException();

            public Task<long> InsertStatementLine(BankStatementLine line, IDbTransaction tx) =>
                throw new NotSupportedException();
        }

        private readonly FakeBankStatementLineRepository _repository = new();
        private readonly StatementLineService _service;

        public StatementLineServiceTest()
        {
            _service = new StatementLineService(_repository);
        }

        private static StatementLineResponseDto Dto(long id, decimal runningBalance) => new()
        {
            Id = id,
            Bank_Account_Id = 3,
            Bank_Account_Name = "Operating",
            Transaction_Id = id + 100,
            Posted_Date = new DateTime(2026, 3, 15),
            Type = "BankReceipt",
            Direction = "in",
            Deposit = 40m,
            Withdrawal = 0m,
            Running_Balance = runningBalance,
            Statement_Status = "Cleared",
            Match_Status = "Matched",
            Source = "Simulated"
        };

        [Fact]
        public async Task GetStatementLines_PassesParametersToRepositoryUnchanged()
        {
            var parameters = new StatementLineParameters
            {
                BankAccountId = 3,
                Month = "2026-03",
                Type = "BankReceipt",
                PageNumber = 2,
                PageSize = 10
            };

            await _service.GetStatementLines(parameters);

            Assert.Same(parameters, Assert.Single(_repository.GetAllCalls));
            Assert.Equal(3L, parameters.BankAccountId);
            Assert.Equal("2026-03", parameters.Month);
            Assert.Equal("BankReceipt", parameters.Type);
        }

        [Fact]
        public async Task GetStatementLines_ReturnsRepositoryItemsUnchanged()
        {
            _repository.GetAllResult = new PagedList<StatementLineResponseDto>(
                new List<StatementLineResponseDto> { Dto(1, 140m), Dto(2, 180m) }, 2, 1, 5);

            var result = await _service.GetStatementLines(new StatementLineParameters());

            Assert.Same(_repository.GetAllResult, result);
            Assert.Equal(new long[] { 1, 2 }, result.Select(l => l.Id).ToArray());
            Assert.Equal(new[] { 140m, 180m }, result.Select(l => l.Running_Balance).ToArray());
        }

        [Fact]
        public async Task GetStatementLines_KeepsMetaData()
        {
            _repository.GetAllResult = new PagedList<StatementLineResponseDto>(
                new List<StatementLineResponseDto> { Dto(6, 100m), Dto(7, 120m) }, 12, 2, 5);

            var result = await _service.GetStatementLines(new StatementLineParameters { PageNumber = 2 });

            Assert.Equal(12, result.MetaData.TotalCount);
            Assert.Equal(2, result.MetaData.CurrentPage);
            Assert.Equal(5, result.MetaData.PageSize);
            Assert.Equal(3, result.MetaData.TotalPages);
        }

        [Fact]
        public async Task GetStatementLines_EmptyPage_ReturnsEmptyListWithMetaData()
        {
            var result = await _service.GetStatementLines(new StatementLineParameters());

            Assert.NotNull(result);
            Assert.Empty(result);
            Assert.Equal(0, result.MetaData.TotalCount);
        }
    }
}
