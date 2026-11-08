using System.Net;
using System.Text;
using Dapper;
using Newtonsoft.Json;
using NPMotors.Repository.Models.Dto;

namespace Module.Tests.Controllers
{
    public class TransferControllerTest : TestBase, IAsyncLifetime
    {
        private readonly string _marker = $"itest-{Guid.NewGuid():N}".Substring(0, 18);
        private long _bankId;
        private long _operatingId;
        private long _payrollId;

        public async Task InitializeAsync()
        {
            using var connection = _repositoryContext.GetDatabaseConnection();
            _bankId = await connection.ExecuteScalarAsync<long>(
                "INSERT INTO bank (name) VALUES (@Name) RETURNING id",
                new { Name = $"{_marker} Bank" });
            _operatingId = await InsertAccountAsync(connection, "Operating", 500m);
            _payrollId = await InsertAccountAsync(connection, "Payroll", 100m);
        }

        public async Task DisposeAsync()
        {
            using var connection = _repositoryContext.GetDatabaseConnection();
            await connection.ExecuteAsync(
                @"DELETE FROM bank_statement_line WHERE bank_account_id IN (SELECT id FROM bank_account WHERE bank_id = @Id)",
                new { Id = _bankId });
            await connection.ExecuteAsync(
                @"DELETE FROM ""transaction"" WHERE bank_account_id IN (SELECT id FROM bank_account WHERE bank_id = @Id)",
                new { Id = _bankId });
            await connection.ExecuteAsync("DELETE FROM bank_account WHERE bank_id = @Id", new { Id = _bankId });
            await connection.ExecuteAsync("DELETE FROM bank WHERE id = @Id", new { Id = _bankId });
        }

        private async Task<long> InsertAccountAsync(System.Data.IDbConnection connection, string name, decimal balance) =>
            await connection.ExecuteScalarAsync<long>(
                @"INSERT INTO bank_account (bank_id, name, type, opening_balance, book_balance, status)
                  VALUES (@BankId, @Name, 'Checking', @Balance, @Balance, 'active') RETURNING id",
                new { BankId = _bankId, Name = $"{_marker} {name}", Balance = balance });

        private static StringContent Json(object body) =>
            new(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");

        private Task<HttpResponseMessage> PostAsync(long source, long destination, decimal amount) =>
            _testClient.PostAsync("Transfer/CreateTransfer", Json(new
            {
                sourceAccountId = source,
                destinationAccountId = destination,
                amount,
                date = "2026-11-07",
                description = "itest"
            }));

        private async Task<int> TransactionCountAsync()
        {
            using var connection = _repositoryContext.GetDatabaseConnection();
            return await connection.ExecuteScalarAsync<int>(
                @"SELECT COUNT(*) FROM ""transaction"" WHERE bank_account_id IN (SELECT id FROM bank_account WHERE bank_id = @Id)",
                new { Id = _bankId });
        }

        [Fact]
        public async Task CreateTransfer_Valid_Returns201WithTransferResponse()
        {
            var response = await PostAsync(_operatingId, _payrollId, 200m);
            var json = await response.Content.ReadAsStringAsync();
            var body = JsonConvert.DeserializeObject<TransferResponseDto>(json)!;

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            Assert.Equal($"TRF-2026-{body.Out_Transaction_Id:D6}", body.Transfer_Reference);
            Assert.True(body.In_Transaction_Id > 0);
            Assert.Equal(200m, body.Amount);
            Assert.Equal(new DateTime(2026, 11, 7), body.Date);
            Assert.Equal($"{_marker} Operating", body.Source_Account_Name);
            Assert.Equal(300m, body.Source_Book_Balance);
            Assert.Equal($"{_marker} Payroll", body.Destination_Account_Name);
            Assert.Equal(300m, body.Destination_Book_Balance);
        }

        [Fact]
        public async Task CreateTransfer_Valid_ResponseUsesTheDiagramFieldNames()
        {
            var response = await PostAsync(_operatingId, _payrollId, 10m);
            var json = await response.Content.ReadAsStringAsync();

            foreach (var field in new[]
            {
                "Transfer_Reference", "Out_Transaction_Id", "In_Transaction_Id", "Amount", "Date",
                "Source_Account_Name", "Source_Book_Balance", "Destination_Account_Name", "Destination_Book_Balance"
            })
                Assert.Contains($"\"{field.ToLowerInvariant()}\"", json.ToLowerInvariant());
        }

        [Fact]
        public async Task CreateTransfer_Valid_SavesTwoTransactions()
        {
            await PostAsync(_operatingId, _payrollId, 10m);

            Assert.Equal(2, await TransactionCountAsync());
        }

        [Fact]
        public async Task CreateTransfer_AmountAboveBalance_Returns400WithErrors()
        {
            var response = await PostAsync(_operatingId, _payrollId, 500.01m);
            var json = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("\"errors\"", json);
            Assert.Contains("cannot exceed the source account's book balance", json.Replace("\\u0027", "'"));
            Assert.Equal(0, await TransactionCountAsync());
        }

        [Fact]
        public async Task CreateTransfer_SameAccount_Returns400WithErrors()
        {
            var response = await PostAsync(_operatingId, _operatingId, 10m);
            var json = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("\"errors\"", json);
            Assert.Contains("must be different", json);
        }

        [Fact]
        public async Task CreateTransfer_AmountZero_Returns400WithErrors()
        {
            var response = await PostAsync(_operatingId, _payrollId, 0m);
            var json = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("Amount must be greater than zero", json);
        }

        [Fact]
        public async Task CreateTransfer_UnknownAccount_Returns400WithErrors()
        {
            var response = await PostAsync(_operatingId, long.MaxValue, 10m);
            var json = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("destination account does not exist", json);
        }

        [Fact]
        public async Task CreateTransfer_DescriptionAbove500Characters_Returns400AndSavesNothing()
        {
            var response = await _testClient.PostAsync("Transfer/CreateTransfer", Json(new
            {
                sourceAccountId = _operatingId,
                destinationAccountId = _payrollId,
                amount = 10m,
                date = "2026-11-07",
                description = new string('x', 501)
            }));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(0, await TransactionCountAsync());
        }

        [Fact]
        public async Task CreateTransfer_MalformedBody_Returns400()
        {
            var response = await _testClient.PostAsync("Transfer/CreateTransfer",
                new StringContent("{ not json", Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task CreateTransfer_NonNumericAccountId_Returns400()
        {
            var response = await _testClient.PostAsync("Transfer/CreateTransfer", Json(new
            {
                sourceAccountId = "abc",
                destinationAccountId = _payrollId,
                amount = 10m,
                date = "2026-11-07"
            }));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task CreateTransfer_GetRequest_IsNotMapped()
        {
            var response = await _testClient.GetAsync("Transfer/CreateTransfer");

            Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        }

        [Theory]
        [InlineData("GET", "Transfer/GetAllTransfers")]
        [InlineData("GET", "Transfer/GetTransferById?id=1")]
        [InlineData("PUT", "Transfer/UpdateTransfer")]
        [InlineData("DELETE", "Transfer/DeleteTransfer?id=1")]
        public async Task OtherTransferActions_AreNotMapped(string method, string url)
        {
            var response = await _testClient.SendAsync(new HttpRequestMessage(new HttpMethod(method), url));

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }
}
