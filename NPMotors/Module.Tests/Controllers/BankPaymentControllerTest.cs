using System.Net;
using System.Text;
using Dapper;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.Models.Enums;
using NPMotors.Repository.Repositories;

namespace Module.Tests.Controllers
{
    // POST /BankPayment/RecordMiscellaneousPayment (Story 21) against the test database.
    // The bank account starts at 50,000 (active); a second account is inactive.
    public class BankPaymentControllerTest : TestBase, IAsyncLifetime
    {
        private const decimal OpeningBalance = 50000m;
        private const string Url = "BankPayment/RecordMiscellaneousPayment";

        private readonly string _marker = $"itest-{Guid.NewGuid():N}".Substring(0, 12);
        private long _bankId;
        private long _accountId;
        private long _inactiveAccountId;
        private long _vendorId;

        public async Task InitializeAsync()
        {
            using var connection = _repositoryContext.GetDatabaseConnection();
            _bankId = await connection.ExecuteScalarAsync<long>(
                "INSERT INTO bank (name) VALUES (@Name) RETURNING id", new { Name = $"{_marker} Bank" });
            _accountId = await InsertAccountAsync("Operating", "active");
            _inactiveAccountId = await InsertAccountAsync("Closed", "inactive");
            _vendorId = (await new VendorRepository(_repositoryContext).AddVendor(new VendorCreateDto
            {
                Name = $"{_marker} Vendor", Type = VendorType.Vehicles, Status = Status.Active
            })).Id;
        }

        public async Task DisposeAsync()
        {
            using var connection = _repositoryContext.GetDatabaseConnection();
            var pattern = new { Pattern = _marker + "%", BankId = _bankId };
            const string markerAccounts = "SELECT id FROM bank_account WHERE bank_id = @BankId";

            await connection.ExecuteAsync($"DELETE FROM bank_payment WHERE bank_account_id IN ({markerAccounts})", pattern);
            await connection.ExecuteAsync($"DELETE FROM bank_statement_line WHERE bank_account_id IN ({markerAccounts})", pattern);
            await connection.ExecuteAsync($@"DELETE FROM ""transaction"" WHERE bank_account_id IN ({markerAccounts})", pattern);
            await connection.ExecuteAsync("DELETE FROM vendor WHERE name LIKE @Pattern", pattern);
            await connection.ExecuteAsync("DELETE FROM bank_account WHERE bank_id = @BankId", pattern);
            await connection.ExecuteAsync("DELETE FROM bank WHERE id = @BankId", pattern);
        }

        private async Task<long> InsertAccountAsync(string name, string status)
        {
            using var connection = _repositoryContext.GetDatabaseConnection();
            return await connection.ExecuteScalarAsync<long>(
                @"INSERT INTO bank_account (bank_id, name, type, opening_balance, book_balance, status)
                  VALUES (@BankId, @Name, 'Checking', @Balance, @Balance, @Status) RETURNING id",
                new { BankId = _bankId, Name = $"{_marker} {name}", Balance = OpeningBalance, Status = status });
        }

        private static StringContent Json(object? body) =>
            new(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");

        private Dictionary<string, object?> Body() => new()
        {
            ["Payee"] = "City Landlord",
            ["Payment_Category"] = "Rent",
            ["Payment_Date"] = "2026-11-08",
            ["Amount"] = 2500m,
            ["Payment_Method"] = "Transfer",
            ["Bank_Account_Id"] = _accountId
        };

        private Task<HttpResponseMessage> PostAsync(Dictionary<string, object?> body) => _testClient.PostAsync(Url, Json(body));

        private static JToken Prop(JToken token, string name) =>
            ((JObject)token).GetValue(name, StringComparison.OrdinalIgnoreCase)!;

        private async Task<(int Transactions, int Lines, int Payments, decimal Book)> StateAsync()
        {
            using var connection = _repositoryContext.GetDatabaseConnection();
            var id = new { Id = _accountId };
            return (
                await connection.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM ""transaction"" WHERE bank_account_id = @Id", id),
                await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM bank_statement_line WHERE bank_account_id = @Id", id),
                await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM bank_payment WHERE bank_account_id = @Id", id),
                await connection.ExecuteScalarAsync<decimal>("SELECT book_balance FROM bank_account WHERE id = @Id", id));
        }

        // ---- 200, response shape (AC 3) ----

        [Fact]
        public async Task RecordMiscellaneousPayment_Valid_Returns200WithThePayment()
        {
            var response = await PostAsync(Body());
            var json = JObject.Parse(await response.Content.ReadAsStringAsync());

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True((long)Prop(json, "id") > 0);
            Assert.StartsWith($"PAY-{DateTime.UtcNow.Year}-", Prop(json, "payment_Number").ToString());
            Assert.Equal("City Landlord", Prop(json, "payee").ToString());
            Assert.Equal("Rent", Prop(json, "payment_Category").ToString());
            Assert.Equal(new DateTime(2026, 11, 8), (DateTime)Prop(json, "payment_Date"));
            Assert.Equal("Transfer", Prop(json, "payment_Method").ToString());
            Assert.Equal(2500m, (decimal)Prop(json, "amount"));
            Assert.Equal(_accountId, (long)Prop(json, "bank_Account_Id"));
            Assert.True((long)Prop(json, "transaction_Id") > 0);
            Assert.Equal("Posted", Prop(json, "status").ToString());
        }

        [Fact]
        public async Task RecordMiscellaneousPayment_ShowsTheDocumentedFields()
        {
            var response = await PostAsync(Body());
            var json = JObject.Parse(await response.Content.ReadAsStringAsync());

            foreach (var name in new[] { "id", "payment_Number", "payment_Date", "payee", "payment_Category", "vendor_Id",
                                         "payment_Method", "reference", "description", "amount", "bank_Account_Id",
                                         "transaction_Id", "book_Balance", "status" })
                Assert.NotNull(json.GetValue(name, StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task RecordMiscellaneousPayment_BookBalanceIsTheBalanceAfterThePayment()
        {
            var response = await PostAsync(Body());
            var json = JObject.Parse(await response.Content.ReadAsStringAsync());

            Assert.Equal(47500m, (decimal)Prop(json, "book_Balance"));
            Assert.Equal(47500m, (await StateAsync()).Book);
        }

        [Fact]
        public async Task RecordMiscellaneousPayment_CreatesOneTransactionOneStatementLineAndOnePayment()
        {
            await PostAsync(Body());

            Assert.Equal((1, 1, 1, 47500m), await StateAsync());
        }

        [Fact]
        public async Task RecordMiscellaneousPayment_OptionalFields_AreSavedAndReturned()
        {
            var body = Body();
            body["Vendor_Id"] = _vendorId;
            body["Reference"] = "CHK-77";
            body["Description"] = "November rent";

            var json = JObject.Parse(await (await PostAsync(body)).Content.ReadAsStringAsync());

            Assert.Equal(_vendorId, (long)Prop(json, "vendor_Id"));
            Assert.Equal("CHK-77", Prop(json, "reference").ToString());
            Assert.Equal("November rent", Prop(json, "description").ToString());
        }

        [Fact]
        public async Task RecordMiscellaneousPayment_WithVendor_DoesNotChangeTheVendorBalance()
        {
            using (var connection = _repositoryContext.GetDatabaseConnection())
                await connection.ExecuteAsync("UPDATE vendor SET balance = 700 WHERE id = @Id", new { Id = _vendorId });
            var body = Body();
            body["Vendor_Id"] = _vendorId;

            await PostAsync(body);

            using var check = _repositoryContext.GetDatabaseConnection();
            Assert.Equal(700m, await check.ExecuteScalarAsync<decimal>("SELECT balance FROM vendor WHERE id = @Id", new { Id = _vendorId }));
        }

        [Fact]
        public async Task RecordMiscellaneousPayment_IsNotLinkedToAnApBillOrVehicle()
        {
            var json = JObject.Parse(await (await PostAsync(Body())).Content.ReadAsStringAsync());

            using var connection = _repositoryContext.GetDatabaseConnection();
            Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM bank_payment WHERE id = @Id AND ap_bill_id IS NULL AND vehicle_id IS NULL",
                new { Id = (long)Prop(json, "id") }));
        }

        [Theory]
        [InlineData("Cash")]
        [InlineData("Check")]
        [InlineData("Card")]
        [InlineData("Transfer")]
        [InlineData("cash")]
        public async Task RecordMiscellaneousPayment_EveryMethod_Returns200(string method)
        {
            var body = Body();
            body["Payment_Method"] = method;

            Assert.Equal(HttpStatusCode.OK, (await PostAsync(body)).StatusCode);
        }

        [Theory]
        [InlineData("Rent")]
        [InlineData("Utilities")]
        [InlineData("Supplies")]
        [InlineData("Payroll")]
        public async Task RecordMiscellaneousPayment_OperatingExpenseCategory_Returns200(string category)
        {
            var body = Body();
            body["Payment_Category"] = category;

            var json = JObject.Parse(await (await PostAsync(body)).Content.ReadAsStringAsync());

            Assert.Equal(category, Prop(json, "payment_Category").ToString());
        }

        // ---- AC 4: inventory purchases and AP settlements are not operating expenses ----

        [Theory]
        [InlineData("Vehicle Purchase")]
        [InlineData("AP Payment")]
        [InlineData("ap payment")]
        public async Task RecordMiscellaneousPayment_NonOperatingCategory_Returns400AndSavesNothing(string category)
        {
            var body = Body();
            body["Payment_Category"] = category;

            var response = await PostAsync(body);
            var json = JObject.Parse(await response.Content.ReadAsStringAsync());

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("not operating expenses", ((JArray)Prop(json, "errors"))[0].ToString());
            Assert.Equal((0, 0, 0, OpeningBalance), await StateAsync());
        }

        // ---- AC 5: each payment is separate ----

        [Fact]
        public async Task RecordMiscellaneousPayment_TwoPayments_AreSeparateRows()
        {
            var first = JObject.Parse(await (await PostAsync(Body())).Content.ReadAsStringAsync());
            var body = Body();
            body["Amount"] = 500m;
            var second = JObject.Parse(await (await PostAsync(body)).Content.ReadAsStringAsync());

            Assert.NotEqual((long)Prop(first, "id"), (long)Prop(second, "id"));
            Assert.NotEqual(Prop(first, "payment_Number").ToString(), Prop(second, "payment_Number").ToString());
            Assert.Equal(47000m, (decimal)Prop(second, "book_Balance"));
            Assert.Equal((2, 2, 2, 47000m), await StateAsync());
            using var connection = _repositoryContext.GetDatabaseConnection();
            Assert.Equal(2500m, await connection.ExecuteScalarAsync<decimal>(
                "SELECT amount FROM bank_payment WHERE id = @Id", new { Id = (long)Prop(first, "id") }));
        }

        [Fact]
        public async Task RecordMiscellaneousPayment_SameBodyTwice_CreatesTwoPayments()
        {
            await PostAsync(Body());
            await PostAsync(Body());

            Assert.Equal((2, 2, 2, 45000m), await StateAsync());
        }

        // ---- AC 1: required fields -> 400, nothing saved ----

        [Theory]
        [InlineData("Payee")]
        [InlineData("Payment_Category")]
        [InlineData("Payment_Method")]
        public async Task RecordMiscellaneousPayment_MissingRequiredText_Returns400AndSavesNothing(string field)
        {
            var body = Body();
            body.Remove(field);

            var response = await PostAsync(body);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal((0, 0, 0, OpeningBalance), await StateAsync());
        }

        [Theory]
        [InlineData("Payee")]
        [InlineData("Payment_Category")]
        [InlineData("Payment_Method")]
        public async Task RecordMiscellaneousPayment_BlankRequiredText_Returns400AndSavesNothing(string field)
        {
            var body = Body();
            body[field] = "   ";

            Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(body)).StatusCode);
            Assert.Equal((0, 0, 0, OpeningBalance), await StateAsync());
        }

        [Fact]
        public async Task RecordMiscellaneousPayment_MissingPaymentDate_Returns400AndSavesNothing()
        {
            var body = Body();
            body.Remove("Payment_Date");

            var response = await PostAsync(body);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal((0, 0, 0, OpeningBalance), await StateAsync());
        }

        [Fact]
        public async Task RecordMiscellaneousPayment_MissingAmount_Returns400AndSavesNothing()
        {
            var body = Body();
            body.Remove("Amount");

            Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(body)).StatusCode);
            Assert.Equal((0, 0, 0, OpeningBalance), await StateAsync());
        }

        [Fact]
        public async Task RecordMiscellaneousPayment_MissingBankAccount_Returns400AndSavesNothing()
        {
            var body = Body();
            body.Remove("Bank_Account_Id");

            Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(body)).StatusCode);
            Assert.Equal((0, 0, 0, OpeningBalance), await StateAsync());
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-5)]
        public async Task RecordMiscellaneousPayment_AmountNotAboveZero_Returns400(double amount)
        {
            var body = Body();
            body["Amount"] = (decimal)amount;

            Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(body)).StatusCode);
            Assert.Equal((0, 0, 0, OpeningBalance), await StateAsync());
        }

        [Fact]
        public async Task RecordMiscellaneousPayment_InvalidMethod_Returns400()
        {
            var body = Body();
            body["Payment_Method"] = "Cheque";

            Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(body)).StatusCode);
            Assert.Equal((0, 0, 0, OpeningBalance), await StateAsync());
        }

        [Theory]
        [InlineData("Payee", 101)]
        [InlineData("Payment_Category", 51)]
        [InlineData("Reference", 101)]
        [InlineData("Description", 201)]
        public async Task RecordMiscellaneousPayment_TextTooLong_Returns400(string field, int length)
        {
            var body = Body();
            body[field] = new string('x', length);

            Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(body)).StatusCode);
            Assert.Equal((0, 0, 0, OpeningBalance), await StateAsync());
        }

        [Theory]
        [InlineData("Payee", 100)]
        [InlineData("Payment_Category", 50)]
        [InlineData("Reference", 100)]
        [InlineData("Description", 200)]
        public async Task RecordMiscellaneousPayment_TextAtTheLimit_Returns200(string field, int length)
        {
            var body = Body();
            body[field] = new string('x', length);

            Assert.Equal(HttpStatusCode.OK, (await PostAsync(body)).StatusCode);
        }

        [Fact]
        public async Task RecordMiscellaneousPayment_EmptyBody_Returns400()
        {
            var response = await _testClient.PostAsync(Url, new StringContent("{}", Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task RecordMiscellaneousPayment_MalformedJson_Returns400()
        {
            var response = await _testClient.PostAsync(Url, new StringContent("{not json", Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        // ---- AC 2 and business rules -> 400 with the message ----

        [Fact]
        public async Task RecordMiscellaneousPayment_AmountAboveTheBalance_Returns400WithTheReason()
        {
            var body = Body();
            body["Amount"] = 50000.01m;

            var response = await PostAsync(body);
            var json = JObject.Parse(await response.Content.ReadAsStringAsync());

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("book balance", ((JArray)Prop(json, "errors"))[0].ToString());
            Assert.Equal((0, 0, 0, OpeningBalance), await StateAsync());
        }

        [Fact]
        public async Task RecordMiscellaneousPayment_AmountEqualToTheBalance_Returns200AndEmptiesTheAccount()
        {
            var body = Body();
            body["Amount"] = OpeningBalance;

            var response = await PostAsync(body);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(0m, (await StateAsync()).Book);
        }

        [Fact]
        public async Task RecordMiscellaneousPayment_InactiveAccount_Returns400()
        {
            var body = Body();
            body["Bank_Account_Id"] = _inactiveAccountId;

            Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(body)).StatusCode);
        }

        [Fact]
        public async Task RecordMiscellaneousPayment_UnknownAccount_Returns400()
        {
            var body = Body();
            body["Bank_Account_Id"] = long.MaxValue;

            Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(body)).StatusCode);
        }

        [Fact]
        public async Task RecordMiscellaneousPayment_UnknownVendor_Returns400AndSavesNothing()
        {
            var body = Body();
            body["Vendor_Id"] = long.MaxValue;

            Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(body)).StatusCode);
            Assert.Equal((0, 0, 0, OpeningBalance), await StateAsync());
        }

        // ---- the actions that are not in the diagram ----

        [Theory]
        [InlineData("GET", "BankPayment/GetBankPaymentById")]
        [InlineData("GET", "BankPayment/GetAllBankPayments")]
        [InlineData("PUT", "BankPayment/UpdateBankPayment")]
        [InlineData("DELETE", "BankPayment/DeleteBankPayment")]
        public async Task NoOtherActions_AreMapped(string verb, string url)
        {
            var request = new HttpRequestMessage(new HttpMethod(verb), url)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };

            var response = await _testClient.SendAsync(request);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        [Theory]
        [InlineData("GET")]
        [InlineData("PUT")]
        [InlineData("DELETE")]
        public async Task RecordMiscellaneousPayment_OnlyPostIsAllowed(string verb)
        {
            var request = new HttpRequestMessage(new HttpMethod(verb), Url);

            var response = await _testClient.SendAsync(request);

            Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        }
    }
}
