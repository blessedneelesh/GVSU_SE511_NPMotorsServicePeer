using System.Net;
using Dapper;
using Newtonsoft.Json;
using NPMotors.Repository.Models;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.Models.Enums;
using NPMotors.Repository.Repositories;

namespace Module.Tests.Controllers
{
    public class TransactionControllerTest : TestBase, IAsyncLifetime
    {
        private TransactionRepository _repo => new(_repositoryContext);
        private readonly string _marker = $"itest-{Guid.NewGuid():N}".Substring(0, 18);
        private long _bankId;
        private long _accountId;

        private class GetAllResponse
        {
            public List<TransactionResponseDto> Items { get; set; } = new();
            public NPMotors.Repository.RequestFeatures.MetaData MetaData { get; set; } = new();
        }

        public async Task InitializeAsync()
        {
            using var connection = _repositoryContext.GetDatabaseConnection();
            _bankId = await connection.ExecuteScalarAsync<long>(
                "INSERT INTO bank (name) VALUES (@Name) RETURNING id",
                new { Name = $"{_marker} Bank" });
            _accountId = await connection.ExecuteScalarAsync<long>(
                @"INSERT INTO bank_account (bank_id, name, type, opening_balance, book_balance, status)
                  VALUES (@BankId, @Name, 'Checking', 100, 100, 'active') RETURNING id",
                new { BankId = _bankId, Name = $"{_marker} Account" });
        }

        public async Task DisposeAsync()
        {
            using var connection = _repositoryContext.GetDatabaseConnection();
            await connection.ExecuteAsync(
                @"DELETE FROM ""transaction"" WHERE bank_account_id IN (SELECT id FROM bank_account WHERE bank_id = @Id)",
                new { Id = _bankId });
            await connection.ExecuteAsync("DELETE FROM bank_account WHERE bank_id = @Id", new { Id = _bankId });
            await connection.ExecuteAsync("DELETE FROM bank WHERE id = @Id", new { Id = _bankId });
        }

        private async Task<long> SeedAsync(
            Direction direction = Direction.In, DateTime? date = null, string sourceNumber = "SRC-1")
        {
            using var connection = _repositoryContext.GetDatabaseConnection();
            using var tx = connection.BeginTransaction();
            var id = await _repo.InsertTransaction(new Transaction
            {
                BankAccountId = _accountId,
                Type = TransactionType.BankReceipt,
                SourceNumber = sourceNumber,
                Direction = direction,
                Date = date ?? new DateTime(2026, 3, 15),
                Amount = 40m,
                Status = TransactionStatus.Posted,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = "itest"
            }, tx);
            tx.Commit();
            return id;
        }

        private async Task<GetAllResponse> GetAllAsync(string query)
        {
            var response = await _testClient.GetAsync($"Transaction/GetAllTransactions?{query}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return JsonConvert.DeserializeObject<GetAllResponse>(await response.Content.ReadAsStringAsync())!;
        }

        [Fact]
        public async Task GetAllTransactions_Returns200WithItemsAndMetaData()
        {
            var id = await SeedAsync();

            var response = await _testClient.GetAsync($"Transaction/GetAllTransactions?BankAccountId={_accountId}");
            var json = await response.Content.ReadAsStringAsync();
            var body = JsonConvert.DeserializeObject<GetAllResponse>(json)!;

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(id, Assert.Single(body.Items).Id);
            Assert.Contains("\"items\"", json);
            Assert.Contains("\"metaData\"", json);
            Assert.Equal(1, body.MetaData.TotalCount);
        }

        [Fact]
        public async Task GetAllTransactions_BindsBankAccountIdFilter()
        {
            await SeedAsync();

            var other = await GetAllAsync($"BankAccountId={long.MaxValue}");

            Assert.Empty(other.Items);
        }

        [Fact]
        public async Task GetAllTransactions_BindsDirectionFilter()
        {
            await SeedAsync(Direction.In);
            var outgoing = await SeedAsync(Direction.Out);

            var body = await GetAllAsync($"BankAccountId={_accountId}&Direction=out");

            Assert.Equal(outgoing, Assert.Single(body.Items).Id);
        }

        [Fact]
        public async Task GetAllTransactions_BindsDateFromAndDateToFilters()
        {
            await SeedAsync(date: new DateTime(2026, 1, 1));
            var inRange = await SeedAsync(date: new DateTime(2026, 2, 15));
            await SeedAsync(date: new DateTime(2026, 4, 1));

            var body = await GetAllAsync($"BankAccountId={_accountId}&DateFrom=2026-02-01&DateTo=2026-02-28");

            Assert.Equal(inRange, Assert.Single(body.Items).Id);
        }

        [Fact]
        public async Task GetAllTransactions_BindsPageNumberAndPageSize()
        {
            await SeedAsync(date: new DateTime(2026, 1, 1));
            await SeedAsync(date: new DateTime(2026, 1, 2));
            await SeedAsync(date: new DateTime(2026, 1, 3));

            var body = await GetAllAsync($"BankAccountId={_accountId}&PageNumber=2&PageSize=2");

            Assert.Single(body.Items);
            Assert.Equal(3, body.MetaData.TotalCount);
            Assert.Equal(2, body.MetaData.CurrentPage);
            Assert.Equal(2, body.MetaData.PageSize);
        }

        [Fact]
        public async Task GetAllTransactions_NonNumericBankAccountId_Returns400()
        {
            var response = await _testClient.GetAsync("Transaction/GetAllTransactions?BankAccountId=abc");

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task GetTransactionById_Existing_Returns200WithDto()
        {
            var id = await SeedAsync(sourceNumber: "RCP-1042");

            var response = await _testClient.GetAsync($"Transaction/GetTransactionById?id={id}");
            var body = JsonConvert.DeserializeObject<TransactionResponseDto>(await response.Content.ReadAsStringAsync());

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(id, body!.Id);
            Assert.Equal("RCP-1042", body.Source_Number);
            Assert.Equal($"TXN-{DateTime.UtcNow.Year}-{id:D6}", body.Number);
            Assert.Equal($"{_marker} Account", body.Bank_Account_Name);
        }

        [Fact]
        public async Task GetTransactionById_Unknown_Returns404()
        {
            var response = await _testClient.GetAsync($"Transaction/GetTransactionById?id={long.MaxValue}");

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        [Fact]
        public async Task GetTransactionById_NonNumericId_Returns400()
        {
            var response = await _testClient.GetAsync("Transaction/GetTransactionById?id=abc");

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        // AC 6: there is no way to edit or delete (or create) a transaction over HTTP.
        [Theory]
        [InlineData("POST", "Transaction/PostTransaction")]
        [InlineData("POST", "Transaction/AddTransaction")]
        [InlineData("PUT", "Transaction/UpdateTransaction?id=1")]
        [InlineData("DELETE", "Transaction/DeleteTransaction?id=1")]
        [InlineData("POST", "Transaction/GetAllTransactions")]
        [InlineData("PUT", "Transaction/GetTransactionById?id=1")]
        [InlineData("DELETE", "Transaction/GetTransactionById?id=1")]
        public async Task WriteEndpoints_AreNotMapped(string method, string url)
        {
            var response = await _testClient.SendAsync(new HttpRequestMessage(new HttpMethod(method), url));

            Assert.Contains(response.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
        }

        [Fact]
        public async Task WriteEndpoints_DoNotChangeAnExistingTransaction()
        {
            var id = await SeedAsync();

            await _testClient.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"Transaction/GetTransactionById?id={id}"));

            var response = await _testClient.GetAsync($"Transaction/GetTransactionById?id={id}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }
}
