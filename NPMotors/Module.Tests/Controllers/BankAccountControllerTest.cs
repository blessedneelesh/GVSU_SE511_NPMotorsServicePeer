using System.Net;
using System.Text;
using Dapper;
using Newtonsoft.Json;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.Models.Enums;
using NPMotors.Repository.Repositories;

namespace Module.Tests.Controllers
{
    public class BankAccountControllerTest : TestBase, IAsyncLifetime
    {
        private BankAccountRepository _repo => new(_repositoryContext);
        private readonly string _marker = $"itest-{Guid.NewGuid():N}".Substring(0, 18);
        private long _bankId;

        private class GetAllResponse
        {
            public List<BankAccountResponseDto> Items { get; set; } = new();
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
            // Remove every account under the test bank (including ones created through the API), then the bank itself.
            using var connection = _repositoryContext.GetDatabaseConnection();
            await connection.ExecuteAsync(
                @"DELETE FROM bank_statement_line WHERE bank_account_id IN (SELECT id FROM bank_account WHERE bank_id = @Id)",
                new { Id = _bankId });
            await connection.ExecuteAsync("DELETE FROM bank_account WHERE bank_id = @Id", new { Id = _bankId });
            await connection.ExecuteAsync("DELETE FROM bank WHERE id = @Id", new { Id = _bankId });
        }

        private BankAccountCreateDto NewAccountDto(string suffix) => new()
        {
            BankId = _bankId,
            Name = $"{_marker} Account {suffix}",
            AccountNumber = "123456789",
            Routing = "021000021",
            Type = "Checking",
            OpeningBalance = 100m,
            Status = Status.Active
        };

        private Task<BankAccountResponseDto> CreateAccountAsync(string suffix = "a") =>
            _repo.AddBankAccount(NewAccountDto(suffix));

        private static StringContent Json(object? body) =>
            new(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");

        private static async Task<T?> ReadAsync<T>(HttpResponseMessage response) =>
            JsonConvert.DeserializeObject<T>(await response.Content.ReadAsStringAsync());

        [Fact]
        public async Task GetAllBankAccounts_Returns200_WithMaskedItemsAndMetaData()
        {
            // Arrange
            var created = await CreateAccountAsync();

            // Act
            var response = await _testClient.GetAsync($"BankAccount/GetAllBankAccounts?SearchTerm={_marker}");
            var json = await response.Content.ReadAsStringAsync();
            var body = JsonConvert.DeserializeObject<GetAllResponse>(json);

            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var item = Assert.Single(body!.Items);
            Assert.Equal(created.Id, item.Id);
            Assert.Equal("*****6789", item.AccountNumber);
            Assert.Contains("\"metaData\"", json);
        }

        [Fact]
        public async Task GetBankAccountById_Returns200_WithFullNumber_And404WhenMissing()
        {
            // Arrange
            var created = await CreateAccountAsync();

            // Act
            var found = await _testClient.GetAsync($"BankAccount/GetBankAccountById?id={created.Id}");
            var missing = await _testClient.GetAsync($"BankAccount/GetBankAccountById?id={long.MaxValue}");

            // Assert
            Assert.Equal(HttpStatusCode.OK, found.StatusCode);
            Assert.Equal("123456789", (await ReadAsync<BankAccountResponseDto>(found))!.AccountNumber);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }

        [Fact]
        public async Task AddBankAccount_Returns201_WithBookBalanceEqualToOpeningBalance()
        {
            // Arrange
            var dto = NewAccountDto("add");
            dto.OpeningBalance = 750.50m;

            // Act
            var response = await _testClient.PostAsync("BankAccount/AddBankAccount", Json(dto));
            var created = await ReadAsync<BankAccountResponseDto>(response);

            // Assert
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            Assert.Equal(750.50m, created!.BookBalance);
        }

        [Fact]
        public async Task AddBankAccount_Returns400_WhenNameIsBlank()
        {
            var dto = NewAccountDto("blank");
            dto.Name = "   ";

            var response = await _testClient.PostAsync("BankAccount/AddBankAccount", Json(dto));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task AddBankAccount_Returns400_WhenTypeIsInvalid()
        {
            var dto = NewAccountDto("type");
            dto.Type = "Bogus";

            var response = await _testClient.PostAsync("BankAccount/AddBankAccount", Json(dto));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task AddBankAccount_Returns400_WhenOpeningBalanceIsNegative()
        {
            var dto = NewAccountDto("neg");
            dto.OpeningBalance = -1m;

            var response = await _testClient.PostAsync("BankAccount/AddBankAccount", Json(dto));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task AddBankAccount_Returns400_WhenBankDoesNotExist()
        {
            var dto = NewAccountDto("nobank");
            dto.BankId = long.MaxValue;

            var response = await _testClient.PostAsync("BankAccount/AddBankAccount", Json(dto));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task UpdateBankAccount_Returns200_AndKeepsBookBalance()
        {
            // Arrange
            var created = await CreateAccountAsync();
            var update = new BankAccountUpdateDto
            {
                BankId = _bankId,
                Name = $"{_marker} Renamed",
                Type = "savings",
                Status = Status.Inactive
            };

            // Act
            var response = await _testClient.PutAsync($"BankAccount/UpdateBankAccount?id={created.Id}", Json(update));
            var updated = await ReadAsync<BankAccountResponseDto>(response);

            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("Savings", updated!.Type);
            Assert.Equal(Status.Inactive, updated.Status);
            Assert.Equal(100m, updated.BookBalance);
        }

        [Fact]
        public async Task UpdateBankAccount_Returns404_WhenMissing()
        {
            var update = new BankAccountUpdateDto { BankId = _bankId, Name = "x", Type = "Other" };

            var response = await _testClient.PutAsync($"BankAccount/UpdateBankAccount?id={long.MaxValue}", Json(update));

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        [Fact]
        public async Task DeleteBankAccount_Returns204_ThenNotFound()
        {
            // Arrange
            var created = await CreateAccountAsync();

            // Act
            var first = await _testClient.DeleteAsync($"BankAccount/DeleteBankAccount?id={created.Id}");
            var second = await _testClient.DeleteAsync($"BankAccount/DeleteBankAccount?id={created.Id}");

            // Assert
            Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
        }

        [Fact]
        public async Task DeleteBankAccount_Returns409_WhenReferenced()
        {
            // Arrange
            var created = await CreateAccountAsync();
            using var connection = _repositoryContext.GetDatabaseConnection();
            var lineId = await connection.ExecuteScalarAsync<long>(
                @"INSERT INTO bank_statement_line (bank_account_id, direction, deposit) VALUES (@Id, 'in', 1) RETURNING id",
                new { Id = created.Id });

            try
            {
                // Act
                var response = await _testClient.DeleteAsync($"BankAccount/DeleteBankAccount?id={created.Id}");

                // Assert
                Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            }
            finally
            {
                await connection.ExecuteAsync("DELETE FROM bank_statement_line WHERE id = @Id", new { Id = lineId });
            }
        }
    }
}
