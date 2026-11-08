using System.Data;
using NPMotors.Repository.Interface;
using NPMotors.Repository.Models;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.RequestFeatures;
using NPMotors.Services.Service;

namespace Module.Tests.Services
{
    public class TransactionServiceTest
    {
        // Hand-written fake: the test projects do not use a mocking library.
        private class FakeTransactionRepository : ITransactionRepository
        {
            public PagedList<TransactionResponseDto> GetAllResult { get; set; } =
                new(new List<TransactionResponseDto>(), 0, 1, 5);
            public TransactionResponseDto? GetByIdResult { get; set; }

            public List<TransactionParameters> GetAllCalls { get; } = new();
            public List<long> GetByIdCalls { get; } = new();

            public Task<PagedList<TransactionResponseDto>> GetAllTransactions(TransactionParameters parameters)
            {
                GetAllCalls.Add(parameters);
                return Task.FromResult(GetAllResult);
            }

            public Task<TransactionResponseDto?> GetTransactionById(long id)
            {
                GetByIdCalls.Add(id);
                return Task.FromResult(GetByIdResult);
            }

            public Task<long> InsertTransaction(Transaction transaction, IDbTransaction tx) =>
                throw new NotSupportedException();
        }

        private readonly FakeTransactionRepository _repository = new();
        private readonly TransactionService _service;

        public TransactionServiceTest()
        {
            _service = new TransactionService(_repository);
        }

        private static TransactionResponseDto Dto(long id) => new()
        {
            Id = id,
            Number = $"TXN-2026-{id:D6}",
            Bank_Account_Id = 3,
            Bank_Account_Name = "Operating",
            Type = "BankReceipt",
            Source_Number = "RCP-" + id,
            Direction = "in",
            Amount = 50m,
            Status = "posted"
        };

        [Fact]
        public async Task GetAllTransactions_ReturnsRepositoryPageWithMetaDataIntact()
        {
            var page = new PagedList<TransactionResponseDto>(new List<TransactionResponseDto> { Dto(1), Dto(2) }, 12, 2, 5);
            _repository.GetAllResult = page;

            var result = await _service.GetAllTransactions(new TransactionParameters { PageNumber = 2, PageSize = 5 });

            Assert.Equal(new long[] { 1, 2 }, result.Select(t => t.Id).ToArray());
            Assert.Equal(12, result.MetaData.TotalCount);
            Assert.Equal(2, result.MetaData.CurrentPage);
            Assert.Equal(5, result.MetaData.PageSize);
            Assert.Equal(3, result.MetaData.TotalPages);
        }

        [Fact]
        public async Task GetAllTransactions_EmptyPage_ReturnsEmptyListWithMetaData()
        {
            _repository.GetAllResult = new PagedList<TransactionResponseDto>(new List<TransactionResponseDto>(), 0, 1, 5);

            var result = await _service.GetAllTransactions(new TransactionParameters());

            Assert.Empty(result);
            Assert.Equal(0, result.MetaData.TotalCount);
        }

        [Fact]
        public async Task GetAllTransactions_PassesParametersToRepositoryUnchanged()
        {
            var parameters = new TransactionParameters
            {
                BankAccountId = 3,
                Direction = "out",
                DateFrom = new DateTime(2026, 1, 1),
                DateTo = new DateTime(2026, 1, 31)
            };

            await _service.GetAllTransactions(parameters);

            Assert.Same(parameters, Assert.Single(_repository.GetAllCalls));
        }

        [Fact]
        public async Task GetTransactionById_Existing_ReturnsRepositoryResult()
        {
            var dto = Dto(7);
            _repository.GetByIdResult = dto;

            var result = await _service.GetTransactionById(7);

            Assert.Same(dto, result);
        }

        [Fact]
        public async Task GetTransactionById_NotFound_ReturnsNull()
        {
            _repository.GetByIdResult = null;

            var result = await _service.GetTransactionById(99);

            Assert.Null(result);
        }

        [Fact]
        public async Task GetTransactionById_PassesIdToRepository()
        {
            await _service.GetTransactionById(42);

            Assert.Equal(42, Assert.Single(_repository.GetByIdCalls));
        }
    }
}
