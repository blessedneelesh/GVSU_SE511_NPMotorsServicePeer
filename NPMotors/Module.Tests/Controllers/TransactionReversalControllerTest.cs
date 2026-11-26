using System.Net;
using System.Text;
using Dapper;
using Newtonsoft.Json;
using NPMotors.Repository.Models.Dto;

namespace Module.Tests.Controllers
{
    // Story 23: POST Transaction/ReverseTransaction/{id}. Data is seeded with plain SQL and removed afterwards.
    public class TransactionReversalControllerTest : TestBase, IAsyncLifetime
    {
        private readonly ReversalSeed _seed;

        private class GetAllResponse
        {
            public List<TransactionResponseDto> Items { get; set; } = new();
        }

        private class ErrorResponse
        {
            public List<string> Errors { get; set; } = new();
        }

        public TransactionReversalControllerTest()
        {
            _seed = new ReversalSeed(() => _repositoryContext.GetDatabaseConnection());
        }

        public Task InitializeAsync() => _seed.InitializeAsync();

        public Task DisposeAsync() => _seed.CleanupAsync();

        private static StringContent Json(object body) =>
            new(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");

        private Task<HttpResponseMessage> ReverseAsync(long id, string reason = "Wrong amount") =>
            _testClient.PostAsync($"Transaction/ReverseTransaction/{id}", Json(new { reason }));

        private static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
            JsonConvert.DeserializeObject<T>(await response.Content.ReadAsStringAsync())!;

        private async Task<T> ScalarAsync<T>(string sql, object? param = null)
        {
            using var c = _seed.Open();
            return await c.ExecuteScalarAsync<T>(sql, param);
        }

        // ---- 200: the new reversal ----

        [Fact]
        public async Task ReverseTransaction_MiscReceipt_Returns200WithOutgoingReversal()
        {
            var s = await _seed.SeedMiscReceiptAsync(1000m);

            var response = await ReverseAsync(s.TransactionId, "Entered twice");
            var body = await ReadAsync<TransactionResponseDto>(response);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.NotEqual(s.TransactionId, body.Id);
            Assert.Equal("Reversal", body.Type);
            Assert.Equal("out", body.Direction);
            Assert.Equal(1000m, body.Amount);
            Assert.Equal("posted", body.Status);
            Assert.Equal(s.TransactionId, body.Reverses_Transaction_Id);
            Assert.Equal("Entered twice", body.Reversal_Reason);
            Assert.Equal(49000m, body.Book_Balance);
            Assert.True(body.Statement_Line_Id > 0);
            Assert.Equal(_seed.AccountId, body.Bank_Account_Id);
        }

        [Fact]
        public async Task ReverseTransaction_ApPayment_Returns200WithIncomingReversalAndRestoresBill()
        {
            var s = await _seed.SeedApPaymentAsync();

            var response = await ReverseAsync(s.TransactionId);
            var body = await ReadAsync<TransactionResponseDto>(response);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("in", body.Direction);
            Assert.Equal(3000m, body.Amount);
            Assert.Equal(53000m, body.Book_Balance);
            using var c = _seed.Open();
            var bill = await c.QuerySingleAsync<(decimal Paid, decimal Balance, string Status)>(
                "SELECT paid, balance, status FROM ap_bill WHERE id = @Id", new { Id = s.BillId });
            Assert.Equal((0m, 8000m, "unpaid"), bill);
            Assert.Equal(8000m, await ScalarAsync<decimal>("SELECT balance FROM vendor WHERE id = @Id", new { Id = _seed.VendorId }));
        }

        // Diagram example: receipt 5 000 on AR 20 000 (Paid 15 000): AR Paid 10 000, Balance 10 000, "Partial"; book 50 000 -> 45 000.
        [Fact]
        public async Task ReverseTransaction_ArReceipt_RestoresArInvoiceSalesInvoiceCustomerAndBankBalance()
        {
            var s = await _seed.SeedArReceiptAsync();

            var response = await ReverseAsync(s.TransactionId);
            var body = await ReadAsync<TransactionResponseDto>(response);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(45000m, body.Book_Balance);
            using var c = _seed.Open();
            var ar = await c.QuerySingleAsync<(decimal Paid, decimal Balance, string Status)>(
                "SELECT paid, balance, status FROM ar_invoice WHERE id = @Id", new { Id = s.ArId });
            var invoice = await c.QuerySingleAsync<(decimal Paid, decimal Balance, string Status)>(
                "SELECT paid, balance, payment_status FROM sales_invoice WHERE id = @Id", new { Id = s.InvoiceId });
            Assert.Equal((10000m, 10000m, "partial"), ar);
            Assert.Equal((10000m, 10000m, "partial"), invoice);
            Assert.Equal(10000m, await ScalarAsync<decimal>("SELECT balance FROM customer WHERE id = @Id", new { Id = _seed.CustomerId }));
            Assert.Equal("Reversed", await ScalarAsync<string>("SELECT status FROM bank_receipt WHERE id = @Id", new { Id = s.SourceId }));
        }

        [Fact]
        public async Task ReverseTransaction_ReceiptThatWasTheOnlyPayment_ReturnsInvoiceToUnpaid()
        {
            var s = await _seed.SeedArReceiptAsync(receiptAmount: 5000m, invoiceTotal: 5000m, paidAfter: 5000m);

            var response = await ReverseAsync(s.TransactionId);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("unpaid", await ScalarAsync<string>("SELECT payment_status FROM sales_invoice WHERE id = @Id", new { Id = s.InvoiceId }));
            Assert.Equal(0m, await ScalarAsync<decimal>("SELECT paid FROM sales_invoice WHERE id = @Id", new { Id = s.InvoiceId }));
        }

        // AC 5: the original and the reversal both stay visible.
        [Fact]
        public async Task ReverseTransaction_OriginalAndReversalBothListedAndLoadable()
        {
            var s = await _seed.SeedMiscReceiptAsync(1000m);
            var reversal = await ReadAsync<TransactionResponseDto>(await ReverseAsync(s.TransactionId));

            var list = await ReadAsync<GetAllResponse>(
                await _testClient.GetAsync($"Transaction/GetAllTransactions?BankAccountId={_seed.AccountId}"));
            var original = await ReadAsync<TransactionResponseDto>(
                await _testClient.GetAsync($"Transaction/GetTransactionById?id={s.TransactionId}"));
            var loadedReversal = await ReadAsync<TransactionResponseDto>(
                await _testClient.GetAsync($"Transaction/GetTransactionById?id={reversal.Id}"));

            Assert.Equal(new[] { s.TransactionId, reversal.Id }.OrderBy(i => i), list.Items.Select(t => t.Id).OrderBy(i => i));
            Assert.Equal("in", original.Direction);
            Assert.Equal("posted", original.Status);
            Assert.Null(original.Reverses_Transaction_Id);
            Assert.Equal(s.TransactionId, loadedReversal.Reverses_Transaction_Id);
            Assert.Equal("Wrong amount", loadedReversal.Reversal_Reason);
        }

        // AC 7: the reversal has its own statement line.
        [Fact]
        public async Task ReverseTransaction_ReversalGetsItsOwnStatementLine()
        {
            var s = await _seed.SeedMiscReceiptAsync(1000m);

            var reversal = await ReadAsync<TransactionResponseDto>(await ReverseAsync(s.TransactionId));

            Assert.Equal(2, await _seed.CountStatementLinesAsync());
            Assert.Equal(reversal.Statement_Line_Id, await ScalarAsync<long>(
                "SELECT id FROM bank_statement_line WHERE transaction_id = @Id", new { Id = reversal.Id }));
        }

        // ---- 404 ----

        [Fact]
        public async Task ReverseTransaction_Unknown_Returns404()
        {
            var response = await ReverseAsync(long.MaxValue);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        // ---- 400: invalid DTO is rejected before anything is saved ----

        [Theory]
        [InlineData("{}")]
        [InlineData("{\"reason\":\"\"}")]
        [InlineData("{\"reason\":null}")]
        [InlineData("{\"reason\":\"   \"}")]
        public async Task ReverseTransaction_MissingOrBlankReason_Returns400AndSavesNothing(string json)
        {
            var s = await _seed.SeedMiscReceiptAsync();

            var response = await _testClient.PostAsync($"Transaction/ReverseTransaction/{s.TransactionId}",
                new StringContent(json, Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(1, await _seed.CountTransactionsAsync());
            Assert.Equal(50000m, await _seed.GetBookBalanceAsync());
        }

        [Fact]
        public async Task ReverseTransaction_ReasonLongerThan500_Returns400AndSavesNothing()
        {
            var s = await _seed.SeedMiscReceiptAsync();

            var response = await ReverseAsync(s.TransactionId, new string('r', 501));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(1, await _seed.CountTransactionsAsync());
        }

        [Fact]
        public async Task ReverseTransaction_ReasonOf500Characters_Returns200()
        {
            var s = await _seed.SeedMiscReceiptAsync();

            var response = await ReverseAsync(s.TransactionId, new string('r', 500));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        [Fact]
        public async Task ReverseTransaction_NoBody_IsRejected()
        {
            var s = await _seed.SeedMiscReceiptAsync();

            var response = await _testClient.PostAsync($"Transaction/ReverseTransaction/{s.TransactionId}", null);

            Assert.Contains(response.StatusCode, new[] { HttpStatusCode.BadRequest, HttpStatusCode.UnsupportedMediaType });
            Assert.Equal(1, await _seed.CountTransactionsAsync());
        }

        // ---- 400: business rules ----

        // AC 6
        [Fact]
        public async Task ReverseTransaction_SameTransactionTwice_SecondReturns400AndOnlyOneReversalExists()
        {
            var s = await _seed.SeedArReceiptAsync();
            Assert.Equal(HttpStatusCode.OK, (await ReverseAsync(s.TransactionId)).StatusCode);

            var second = await ReverseAsync(s.TransactionId);
            var error = await ReadAsync<ErrorResponse>(second);

            Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
            Assert.Contains("already been reversed", Assert.Single(error.Errors));
            Assert.Equal(2, await _seed.CountTransactionsAsync());
            Assert.Equal(45000m, await _seed.GetBookBalanceAsync());
            Assert.Equal(10000m, await ScalarAsync<decimal>("SELECT paid FROM ar_invoice WHERE id = @Id", new { Id = s.ArId }));
        }

        [Fact]
        public async Task ReverseTransaction_ReversingAReversal_Returns400()
        {
            var s = await _seed.SeedMiscReceiptAsync();
            var reversal = await ReadAsync<TransactionResponseDto>(await ReverseAsync(s.TransactionId));

            var response = await ReverseAsync(reversal.Id);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(2, await _seed.CountTransactionsAsync());
        }

        [Fact]
        public async Task ReverseTransaction_TransactionThatIsNotAReceiptOrPayment_Returns400()
        {
            var s = await _seed.SeedPlainTransactionAsync();

            var response = await ReverseAsync(s.TransactionId);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(1, await _seed.CountTransactionsAsync());
        }

        [Fact]
        public async Task ReverseTransaction_ReceiptAboveBookBalance_Returns400AndChangesNothing()
        {
            var s = await _seed.SeedArReceiptAsync();
            await _seed.SetBookBalanceAsync(100m);

            var response = await ReverseAsync(s.TransactionId);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(100m, await _seed.GetBookBalanceAsync());
            Assert.Equal(1, await _seed.CountTransactionsAsync());
            Assert.Equal("Posted", await ScalarAsync<string>("SELECT status FROM bank_receipt WHERE id = @Id", new { Id = s.SourceId }));
            Assert.Equal(15000m, await ScalarAsync<decimal>("SELECT paid FROM ar_invoice WHERE id = @Id", new { Id = s.ArId }));
        }

        // ---- routing ----

        [Fact]
        public async Task ReverseTransaction_NonNumericId_Returns404()
        {
            var response = await _testClient.PostAsync("Transaction/ReverseTransaction/abc", Json(new { reason = "x" }));

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        [Fact]
        public async Task ReverseTransaction_WithoutId_IsNotMapped()
        {
            var response = await _testClient.PostAsync("Transaction/ReverseTransaction", Json(new { reason = "x" }));

            Assert.Contains(response.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
        }

        [Theory]
        [InlineData("GET")]
        [InlineData("PUT")]
        [InlineData("DELETE")]
        public async Task ReverseTransaction_OtherHttpMethods_AreNotMapped(string method)
        {
            var s = await _seed.SeedMiscReceiptAsync();

            var response = await _testClient.SendAsync(
                new HttpRequestMessage(new HttpMethod(method), $"Transaction/ReverseTransaction/{s.TransactionId}"));

            Assert.Contains(response.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
            Assert.Equal(1, await _seed.CountTransactionsAsync());
        }
    }
}
