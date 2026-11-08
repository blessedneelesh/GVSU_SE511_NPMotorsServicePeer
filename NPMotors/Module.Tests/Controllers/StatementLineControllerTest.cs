using System.Net;
using Dapper;
using Newtonsoft.Json;
using NPMotors.Repository.Models;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.Models.Enums;
using NPMotors.Repository.Repositories;

namespace Module.Tests.Controllers
{
    public class StatementLineControllerTest : TestBase, IAsyncLifetime
    {
        private BankStatementLineRepository _repo => new(_repositoryContext);
        private readonly string _marker = $"itest-{Guid.NewGuid():N}".Substring(0, 18);
        private long _bankId;
        private long _accountId;

        private class GetAllResponse
        {
            public List<StatementLineResponseDto> Items { get; set; } = new();
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
                "DELETE FROM bank_statement_line WHERE bank_account_id IN (SELECT id FROM bank_account WHERE bank_id = @Id)",
                new { Id = _bankId });
            await connection.ExecuteAsync("DELETE FROM bank_account WHERE bank_id = @Id", new { Id = _bankId });
            await connection.ExecuteAsync("DELETE FROM bank WHERE id = @Id", new { Id = _bankId });
        }

        private async Task<long> SeedAsync(
            DateTime? date = null, Direction direction = Direction.In,
            TransactionType type = TransactionType.BankReceipt, decimal runningBalance = 140m)
        {
            using var connection = _repositoryContext.GetDatabaseConnection();
            using var tx = connection.BeginTransaction();
            var id = await _repo.InsertStatementLine(new BankStatementLine
            {
                BankAccountId = _accountId,
                PostedDate = date ?? new DateTime(2026, 3, 15),
                Description = "test line",
                Party = "Acme",
                Type = type,
                Direction = direction,
                Deposit = direction == Direction.In ? 40m : 0m,
                Withdrawal = direction == Direction.Out ? 40m : 0m,
                RunningBalance = runningBalance,
                StatementStatus = StatementStatus.Cleared,
                MatchStatus = MatchStatus.Matched,
                Source = StatementSource.Simulated,
                CreatedAt = DateTime.UtcNow
            }, tx);
            tx.Commit();
            return id;
        }

        private async Task<GetAllResponse> GetAllAsync(string query)
        {
            var response = await _testClient.GetAsync($"StatementLine/GetStatementLines?{query}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return JsonConvert.DeserializeObject<GetAllResponse>(await response.Content.ReadAsStringAsync())!;
        }

        [Fact]
        public async Task GetStatementLines_Returns200WithItemsAndMetaData()
        {
            var id = await SeedAsync();

            var response = await _testClient.GetAsync($"StatementLine/GetStatementLines?BankAccountId={_accountId}");
            var json = await response.Content.ReadAsStringAsync();
            var body = JsonConvert.DeserializeObject<GetAllResponse>(json)!;

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("\"items\"", json);
            Assert.Contains("\"metaData\"", json);
            var line = Assert.Single(body.Items);
            Assert.Equal(id, line.Id);
            Assert.Equal(1, body.MetaData.TotalCount);
        }

        [Fact]
        public async Task GetStatementLines_ReturnsEveryDtoField()
        {
            await SeedAsync(new DateTime(2026, 3, 15), Direction.Out, TransactionType.BankPayment, 60m);

            var line = Assert.Single((await GetAllAsync($"BankAccountId={_accountId}")).Items);

            Assert.Equal(_accountId, line.Bank_Account_Id);
            Assert.Equal($"{_marker} Account", line.Bank_Account_Name);
            Assert.Null(line.Transaction_Id);
            Assert.Equal(new DateTime(2026, 3, 15), line.Posted_Date);
            Assert.Equal("BankPayment", line.Type);
            Assert.Equal("out", line.Direction);
            Assert.Equal(0m, line.Deposit);
            Assert.Equal(40m, line.Withdrawal);
            Assert.Equal(60m, line.Running_Balance);
            Assert.Equal("Cleared", line.Statement_Status);
            Assert.Equal("Matched", line.Match_Status);
            Assert.Equal("Simulated", line.Source);
        }

        [Fact]
        public async Task GetStatementLines_BindsBankAccountIdFilter()
        {
            await SeedAsync();

            var other = await GetAllAsync($"BankAccountId={long.MaxValue}");

            Assert.Empty(other.Items);
        }

        [Fact]
        public async Task GetStatementLines_BindsMonthFilter()
        {
            await SeedAsync(new DateTime(2026, 2, 10));
            var march = await SeedAsync(new DateTime(2026, 3, 10));
            await SeedAsync(new DateTime(2026, 4, 10));

            var body = await GetAllAsync($"BankAccountId={_accountId}&Month=2026-03");

            Assert.Equal(march, Assert.Single(body.Items).Id);
        }

        [Fact]
        public async Task GetStatementLines_BindsTypeFilter()
        {
            await SeedAsync(type: TransactionType.BankReceipt);
            var payment = await SeedAsync(type: TransactionType.BankPayment);

            var body = await GetAllAsync($"BankAccountId={_accountId}&Type=BankPayment");

            Assert.Equal(payment, Assert.Single(body.Items).Id);
        }

        [Fact]
        public async Task GetStatementLines_BindsPageNumberAndPageSize()
        {
            await SeedAsync(new DateTime(2026, 1, 1));
            await SeedAsync(new DateTime(2026, 1, 2));
            await SeedAsync(new DateTime(2026, 1, 3));

            var body = await GetAllAsync($"BankAccountId={_accountId}&PageNumber=2&PageSize=2");

            Assert.Single(body.Items);
            Assert.Equal(3, body.MetaData.TotalCount);
            Assert.Equal(2, body.MetaData.CurrentPage);
            Assert.Equal(2, body.MetaData.PageSize);
        }

        [Fact]
        public async Task GetStatementLines_ListsInStatementDateOrder()
        {
            var late = await SeedAsync(new DateTime(2026, 3, 20));
            var early = await SeedAsync(new DateTime(2026, 3, 5));

            var body = await GetAllAsync($"BankAccountId={_accountId}");

            Assert.Equal(new[] { early, late }, body.Items.Select(l => l.Id).ToArray());
        }

        [Fact]
        public async Task GetStatementLines_EmptyFilterValues_ReturnAllOfTheAccount()
        {
            await SeedAsync();
            await SeedAsync();

            var body = await GetAllAsync($"BankAccountId={_accountId}&Month=&Type=");

            Assert.Equal(2, body.Items.Count);
        }

        [Fact]
        public async Task GetStatementLines_NonNumericBankAccountId_Returns400()
        {
            var response = await _testClient.GetAsync("StatementLine/GetStatementLines?BankAccountId=abc");

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        // Read-only screen: lines are created only by PostTransaction, never over HTTP.
        [Theory]
        [InlineData("POST", "StatementLine/GetStatementLines")]
        [InlineData("PUT", "StatementLine/GetStatementLines")]
        [InlineData("DELETE", "StatementLine/GetStatementLines")]
        [InlineData("POST", "StatementLine/AddStatementLine")]
        [InlineData("PUT", "StatementLine/UpdateStatementLine?id=1")]
        [InlineData("DELETE", "StatementLine/DeleteStatementLine?id=1")]
        [InlineData("GET", "StatementLine/GetStatementLineById?id=1")]
        public async Task WriteEndpoints_AreNotMapped(string method, string url)
        {
            var response = await _testClient.SendAsync(new HttpRequestMessage(new HttpMethod(method), url));

            Assert.Contains(response.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
        }
    }
}
