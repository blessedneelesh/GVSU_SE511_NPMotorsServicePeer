using Dapper;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.Models.Enums;
using NPMotors.Repository.Repositories;
using NPMotors.Repository.RequestFeatures;

namespace NPMotors.Repository.Tests.DataAccess
{
    public class BankAccountRepositoryTest : TestBase, IAsyncLifetime
    {
        private readonly BankAccountRepository _repository;
        private readonly string _marker = $"itest-{Guid.NewGuid():N}".Substring(0, 18);
        private readonly List<long> _insertedIds = new();
        private long _bankId;

        public BankAccountRepositoryTest()
        {
            _repository = new BankAccountRepository(_repositoryContext);
        }

        public async Task InitializeAsync()
        {
            using var connection = _repositoryContext.GetDatabaseConnection();
            _bankId = await connection.ExecuteScalarAsync<long>(
                "INSERT INTO bank (name) VALUES (@Name) RETURNING id",
                new { Name = $"{_marker} Bank" });
        }

        public async Task DisposeAsync()
        {
            foreach (var id in _insertedIds)
            {
                await _repository.DeleteBankAccount(id);
            }

            using var connection = _repositoryContext.GetDatabaseConnection();
            await connection.ExecuteAsync("DELETE FROM bank WHERE id = @Id", new { Id = _bankId });
        }

        private async Task<BankAccountResponseDto> InsertTestAccountAsync(
            string suffix = "a", Status status = Status.Active, string type = "Checking",
            decimal openingBalance = 100m, string accountNumber = "123456789")
        {
            var created = await _repository.AddBankAccount(new BankAccountCreateDto
            {
                BankId = _bankId,
                Name = $"{_marker} Account {suffix}",
                AccountNumber = accountNumber,
                Routing = "021000021",
                Type = type,
                OpeningBalance = openingBalance,
                Status = status
            });

            _insertedIds.Add(created.Id);
            return created;
        }

        [Fact]
        public async Task GetAllBankAccounts_SearchIsCaseInsensitive()
        {
            // Arrange
            var inserted = await InsertTestAccountAsync();

            // Act
            var result = await _repository.GetAllBankAccounts(
                new BankAccountParameters { SearchTerm = _marker.ToUpperInvariant() });

            // Assert
            Assert.Contains(result, a => a.Id == inserted.Id);
        }

        [Fact]
        public async Task GetAllBankAccounts_MasksAccountNumber()
        {
            // Arrange
            var inserted = await InsertTestAccountAsync(accountNumber: "123456789");

            // Act
            var result = await _repository.GetAllBankAccounts(new BankAccountParameters { SearchTerm = _marker });

            // Assert
            Assert.Equal("*****6789", result.Single(a => a.Id == inserted.Id).AccountNumber);
        }

        [Fact]
        public async Task GetBankAccountById_ReturnsFullAccountNumber()
        {
            // Arrange
            var inserted = await InsertTestAccountAsync(accountNumber: "123456789");

            // Act
            var found = await _repository.GetBankAccountById(inserted.Id);

            // Assert
            Assert.Equal("123456789", found!.AccountNumber);
            Assert.Equal(_bankId, found.BankId);
        }

        [Fact]
        public async Task GetAllBankAccounts_FiltersByStatusTypeAndBank()
        {
            // Arrange
            var active = await InsertTestAccountAsync("active", Status.Active, "Checking");
            var inactive = await InsertTestAccountAsync("inactive", Status.Inactive, "Savings");

            // Act
            var byStatus = await _repository.GetAllBankAccounts(
                new BankAccountParameters { SearchTerm = _marker, Status = Status.Inactive });
            var byType = await _repository.GetAllBankAccounts(
                new BankAccountParameters { SearchTerm = _marker, Type = "CHECKING" });
            var byBank = await _repository.GetAllBankAccounts(
                new BankAccountParameters { BankId = _bankId });

            // Assert
            Assert.Equal(inactive.Id, Assert.Single(byStatus).Id);
            Assert.Equal(active.Id, Assert.Single(byType).Id);
            Assert.Equal(2, byBank.Count);
        }

        [Fact]
        public async Task GetAllBankAccounts_ReturnsRequestedPageAndMetaData()
        {
            // Arrange
            await InsertTestAccountAsync("one");
            await InsertTestAccountAsync("two");

            // Act
            var page2 = await _repository.GetAllBankAccounts(
                new BankAccountParameters { SearchTerm = _marker, PageNumber = 2, PageSize = 1 });

            // Assert
            Assert.Single(page2);
            Assert.Equal(2, page2.MetaData.TotalCount);
            Assert.Equal(2, page2.MetaData.CurrentPage);
        }

        [Fact]
        public async Task GetAllBankAccounts_ReturnsEmpty_WhenNothingMatches()
        {
            var result = await _repository.GetAllBankAccounts(
                new BankAccountParameters { SearchTerm = $"{_marker}-nomatch" });

            Assert.Empty(result);
            Assert.Equal(0, result.MetaData.TotalCount);
        }

        [Fact]
        public async Task AddBankAccount_SetsBookBalanceToOpeningBalance()
        {
            var created = await InsertTestAccountAsync(openingBalance: 2500.75m);

            Assert.Equal(2500.75m, created.BookBalance);
        }

        [Fact]
        public async Task UpdateBankAccount_DoesNotChangeBookBalance_AndCanSetInactive()
        {
            // Arrange
            var created = await InsertTestAccountAsync(openingBalance: 300m);

            // Act
            var updated = await _repository.UpdateBankAccount(created.Id, new BankAccountUpdateDto
            {
                BankId = _bankId,
                Name = $"{_marker} Renamed",
                AccountNumber = "999988887777",
                Routing = "021000021",
                Type = "Savings",
                Status = Status.Inactive
            });

            // Assert
            Assert.NotNull(updated);
            Assert.Equal(300m, updated!.BookBalance);
            Assert.Equal(Status.Inactive, updated.Status);
            Assert.Equal("Savings", updated.Type);
        }

        [Fact]
        public async Task UpdateBankAccount_ReturnsNull_WhenIdDoesNotExist()
        {
            var updated = await _repository.UpdateBankAccount(long.MaxValue, new BankAccountUpdateDto
            {
                BankId = _bankId,
                Name = "x",
                Type = "Other"
            });

            Assert.Null(updated);
        }

        [Fact]
        public async Task DeleteBankAccount_RemovesRow_AndReportsNotReferenced()
        {
            // Arrange
            var created = await InsertTestAccountAsync();

            // Act
            var referenced = await _repository.IsBankAccountReferenced(created.Id);
            var deleted = await _repository.DeleteBankAccount(created.Id);

            // Assert
            Assert.False(referenced);
            Assert.True(deleted);
            Assert.Null(await _repository.GetBankAccountById(created.Id));
        }

        [Fact]
        public async Task BankExists_ReflectsBankRows()
        {
            Assert.True(await _repository.BankExists(_bankId));
            Assert.False(await _repository.BankExists(long.MaxValue));
        }
    }
}
