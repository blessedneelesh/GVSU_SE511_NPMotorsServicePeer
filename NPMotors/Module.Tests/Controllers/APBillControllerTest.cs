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
    // POST /APBill/RecordVendorPayment/{apBillId} and GET /APBill/GetPaymentHistory/{apBillId} (Story 20)
    // against the test database. Each bill is inserted directly: amount 4000, paid 0, unpaid, balance 4000.
    // The vendor balance is kept equal to the sum of the vendor's open bill balances.
    public class APBillControllerTest : TestBase, IAsyncLifetime
    {
        private const decimal BillAmount = 4000m;
        private const decimal OpeningBalance = 5000m;
        private const decimal LowBalance = 100m;

        private readonly string _marker = $"itest-{Guid.NewGuid():N}".Substring(0, 12);
        private long _bankId;
        private long _accountId;
        private long _secondAccountId;
        private long _lowAccountId;
        private long _inactiveAccountId;
        private long _vendorId;
        private int _billCount;

        public async Task InitializeAsync()
        {
            using var connection = _repositoryContext.GetDatabaseConnection();
            _bankId = await connection.ExecuteScalarAsync<long>(
                "INSERT INTO bank (name) VALUES (@Name) RETURNING id", new { Name = $"{_marker} Bank" });
            _accountId = await InsertAccountAsync("Operating", "active", OpeningBalance);
            _secondAccountId = await InsertAccountAsync("Savings", "active", OpeningBalance);
            _lowAccountId = await InsertAccountAsync("Low", "active", LowBalance);
            _inactiveAccountId = await InsertAccountAsync("Closed", "inactive", OpeningBalance);

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
            await connection.ExecuteAsync("DELETE FROM ap_bill WHERE vendor_id IN (SELECT id FROM vendor WHERE name LIKE @Pattern)", pattern);
            await connection.ExecuteAsync("DELETE FROM vendor WHERE name LIKE @Pattern", pattern);
            await connection.ExecuteAsync("DELETE FROM bank_account WHERE bank_id = @BankId", pattern);
            await connection.ExecuteAsync("DELETE FROM bank WHERE id = @BankId", pattern);
        }

        private async Task<long> InsertAccountAsync(string name, string status, decimal balance)
        {
            using var connection = _repositoryContext.GetDatabaseConnection();
            return await connection.ExecuteScalarAsync<long>(
                @"INSERT INTO bank_account (bank_id, name, type, opening_balance, book_balance, status)
                  VALUES (@BankId, @Name, 'Checking', @Balance, @Balance, @Status) RETURNING id",
                new { BankId = _bankId, Name = $"{_marker} {name}", Balance = balance, Status = status });
        }

        private async Task<long> CreateBillAsync(decimal amount = BillAmount, decimal paid = 0m, string status = "unpaid")
        {
            using var connection = _repositoryContext.GetDatabaseConnection();
            var id = await connection.ExecuteScalarAsync<long>(
                @"INSERT INTO ap_bill (number, vendor_id, date, due_date, amount, paid, balance, status, created_by)
                  VALUES (@Number, @VendorId, '2026-10-01', '2026-12-01', @Amount, @Paid, @Balance, @Status, 'itest')
                  RETURNING id",
                new
                {
                    Number = $"AP-{_marker}-{++_billCount}", VendorId = _vendorId,
                    Amount = amount, Paid = paid, Balance = amount - paid, Status = status
                });
            await connection.ExecuteAsync(
                @"UPDATE vendor SET balance = (SELECT COALESCE(SUM(balance), 0) FROM ap_bill WHERE vendor_id = @VendorId AND balance > 0)
                  WHERE id = @VendorId", new { VendorId = _vendorId });
            return id;
        }

        private static StringContent Json(object? body) =>
            new(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");

        private Task<HttpResponseMessage> PayAsync(long billId, object body) =>
            _testClient.PostAsync($"APBill/RecordVendorPayment/{billId}", Json(body));

        private object PaymentBody(decimal amount = 1500m, long? accountId = null, string method = "Check",
            string? reference = "CHK-1001", string date = "2026-11-08") => new
        {
            Payment_Date = date, Amount = amount, Payment_Method = method, Reference = reference,
            Bank_Account_Id = accountId ?? _accountId
        };

        private static async Task<ApBillResponseDto> ReadAsync(HttpResponseMessage response) =>
            JsonConvert.DeserializeObject<ApBillResponseDto>(await response.Content.ReadAsStringAsync())!;

        private static async Task<JObject> ReadJsonAsync(HttpResponseMessage response) =>
            JObject.Parse(await response.Content.ReadAsStringAsync());

        private async Task<T> QueryAsync<T>(string sql, object param)
        {
            using var connection = _repositoryContext.GetDatabaseConnection();
            return await connection.ExecuteScalarAsync<T>(sql, param) ?? default!;
        }

        private Task<decimal> BookBalanceAsync(long accountId) =>
            QueryAsync<decimal>("SELECT book_balance FROM bank_account WHERE id = @Id", new { Id = accountId });

        private Task<decimal> VendorBalanceAsync() =>
            QueryAsync<decimal>("SELECT balance FROM vendor WHERE id = @Id", new { Id = _vendorId });

        private Task<int> PaymentCountAsync(long billId) =>
            QueryAsync<int>("SELECT COUNT(*) FROM bank_payment WHERE ap_bill_id = @Id", new { Id = billId });

        private async Task AssertNothingSavedAsync(long billId)
        {
            Assert.Equal(0, await PaymentCountAsync(billId));
            Assert.Equal(0m, await QueryAsync<decimal>("SELECT paid FROM ap_bill WHERE id = @Id", new { Id = billId }));
            Assert.Equal(BillAmount, await QueryAsync<decimal>("SELECT balance FROM ap_bill WHERE id = @Id", new { Id = billId }));
            Assert.Equal("unpaid", await QueryAsync<string>("SELECT status FROM ap_bill WHERE id = @Id", new { Id = billId }));
            Assert.Equal(OpeningBalance, await BookBalanceAsync(_accountId));
            Assert.Equal(OpeningBalance, await BookBalanceAsync(_secondAccountId));
            Assert.Equal(LowBalance, await BookBalanceAsync(_lowAccountId));
            Assert.Equal(OpeningBalance, await BookBalanceAsync(_inactiveAccountId));
            Assert.Equal(BillAmount, await VendorBalanceAsync());
            Assert.Equal(0, await QueryAsync<int>(
                @"SELECT COUNT(*) FROM ""transaction"" WHERE bank_account_id IN (SELECT id FROM bank_account WHERE bank_id = @BankId)",
                new { BankId = _bankId }));
        }

        // ---- RecordVendorPayment: 200 (AC 3, 4, 5, 7) ----

        [Fact]
        public async Task RecordVendorPayment_Partial_Returns200WithUpdatedBillAndPaymentHistory()
        {
            var billId = await CreateBillAsync();

            var response = await PayAsync(billId, PaymentBody(1500m));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await ReadAsync(response);
            Assert.Equal(billId, body.Id);
            Assert.Equal(BillAmount, body.Amount);
            Assert.Equal(1500m, body.Paid);
            Assert.Equal(2500m, body.Balance);
            Assert.Equal("partial", body.Status);
            Assert.Equal(2500m, body.Vendor_Balance);
            var payment = Assert.Single(body.Payments);
            Assert.Equal(1500m, payment.Amount);
            Assert.Equal("Check", payment.Payment_Method);
            Assert.Equal("CHK-1001", payment.Reference);
            Assert.Equal(_accountId, payment.Bank_Account_Id);
        }

        [Fact]
        public async Task RecordVendorPayment_FullBalance_Returns200WithPaidStatusAndZeroBalances()
        {
            var billId = await CreateBillAsync();

            var response = await PayAsync(billId, PaymentBody(BillAmount));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await ReadAsync(response);
            Assert.Equal(BillAmount, body.Paid);
            Assert.Equal(0m, body.Balance);
            Assert.Equal("paid", body.Status);
            Assert.Equal(0m, body.Vendor_Balance);
            Assert.Equal("paid", await QueryAsync<string>("SELECT status FROM ap_bill WHERE id = @Id", new { Id = billId }));
        }

        [Fact]
        public async Task RecordVendorPayment_Returns200WithTheDocumentedFields()
        {
            var billId = await CreateBillAsync();

            var json = await ReadJsonAsync(await PayAsync(billId, PaymentBody()));

            foreach (var name in new[] { "id", "number", "vendor_Id", "vehicle_Id", "date", "due_Date", "amount", "paid", "balance", "status", "vendor_Balance", "payments" })
                Assert.NotNull(json.GetValue(name, StringComparison.OrdinalIgnoreCase));
            var payment = (JObject)((JArray)json.GetValue("payments", StringComparison.OrdinalIgnoreCase)!)[0];
            foreach (var name in new[] { "id", "payment_Number", "payment_Date", "payment_Method", "reference", "amount", "bank_Account_Id", "transaction_Id", "status" })
                Assert.NotNull(payment.GetValue(name, StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task RecordVendorPayment_UpdatesPaidBalanceAndStatusTogetherAndKeepsTheAmount()
        {
            var billId = await CreateBillAsync();

            await PayAsync(billId, PaymentBody(1500m));

            Assert.Equal(1500m, await QueryAsync<decimal>("SELECT paid FROM ap_bill WHERE id = @Id", new { Id = billId }));
            Assert.Equal(2500m, await QueryAsync<decimal>("SELECT balance FROM ap_bill WHERE id = @Id", new { Id = billId }));
            Assert.Equal("partial", await QueryAsync<string>("SELECT status FROM ap_bill WHERE id = @Id", new { Id = billId }));
            Assert.Equal(BillAmount, await QueryAsync<decimal>("SELECT amount FROM ap_bill WHERE id = @Id", new { Id = billId }));
        }

        [Fact]
        public async Task RecordVendorPayment_UpdatesTheVendorBalanceToTheSumOfTheVendorsOpenBills()
        {
            var billId = await CreateBillAsync(BillAmount);
            await CreateBillAsync(1000m);
            Assert.Equal(5000m, await VendorBalanceAsync());

            await PayAsync(billId, PaymentBody(1500m));

            Assert.Equal(3500m, await VendorBalanceAsync());
        }

        [Fact]
        public async Task RecordVendorPayment_LowersTheSelectedAccountsBookBalanceOnly()
        {
            var billId = await CreateBillAsync();

            await PayAsync(billId, PaymentBody(1500m, _secondAccountId));

            Assert.Equal(OpeningBalance, await BookBalanceAsync(_accountId));
            Assert.Equal(OpeningBalance - 1500m, await BookBalanceAsync(_secondAccountId));
        }

        [Fact]
        public async Task RecordVendorPayment_CreatesPaymentOutTransactionAndStatementLine()
        {
            var billId = await CreateBillAsync();

            await PayAsync(billId, PaymentBody(1500m, _secondAccountId, "Card", "POS-5", "2026-11-08"));

            Assert.Equal(1, await QueryAsync<int>(
                @"SELECT COUNT(*) FROM bank_payment p
                  JOIN ""transaction"" t ON t.id = p.transaction_id AND t.bank_account_id = @AccountId AND t.amount = 1500
                       AND t.direction = 'out'::direction_enum AND t.type = 'BankPayment'
                  JOIN bank_statement_line s ON s.transaction_id = t.id AND s.withdrawal = 1500 AND s.deposit = 0
                  WHERE p.ap_bill_id = @BillId AND p.bank_account_id = @AccountId AND p.vendor_id = @VendorId
                    AND p.payment_method = 'Card' AND p.reference = 'POS-5' AND p.amount = 1500
                    AND p.payment_date = DATE '2026-11-08'",
                new { BillId = billId, AccountId = _secondAccountId, VendorId = _vendorId }));
        }

        [Fact]
        public async Task RecordVendorPayment_PaymentNumberIsPayYearPlusIdPaddedToSixDigits()
        {
            var billId = await CreateBillAsync();

            var body = await ReadAsync(await PayAsync(billId, PaymentBody()));

            var payment = Assert.Single(body.Payments);
            Assert.Equal($"PAY-{DateTime.UtcNow.Year}-{payment.Id:D6}", payment.Payment_Number);
        }

        [Fact]
        public async Task RecordVendorPayment_TwoPayments_CreateTwoSeparatePaymentsAndKeepTheEarlierOneUnchanged()
        {
            var billId = await CreateBillAsync();
            var first = await ReadAsync(await PayAsync(billId, PaymentBody(1000m, reference: "FIRST", date: "2026-11-01")));
            var firstPayment = Assert.Single(first.Payments);

            var second = await ReadAsync(await PayAsync(billId, PaymentBody(2000m, reference: "SECOND", date: "2026-11-02")));

            Assert.Equal(2, await PaymentCountAsync(billId));
            Assert.Equal(2, await QueryAsync<int>(
                "SELECT COUNT(DISTINCT transaction_id) FROM bank_payment WHERE ap_bill_id = @Id", new { Id = billId }));
            Assert.Equal(2, await QueryAsync<int>(
                "SELECT COUNT(*) FROM bank_statement_line WHERE transaction_id IN (SELECT transaction_id FROM bank_payment WHERE ap_bill_id = @Id)",
                new { Id = billId }));
            Assert.Equal(1000m, await QueryAsync<decimal>("SELECT amount FROM bank_payment WHERE id = @Id", new { Id = firstPayment.Id }));
            Assert.Equal("FIRST", await QueryAsync<string>("SELECT reference FROM bank_payment WHERE id = @Id", new { Id = firstPayment.Id }));
            Assert.Equal(3000m, second.Paid);
            Assert.Equal(1000m, second.Balance);
            Assert.Equal(OpeningBalance - 3000m, await BookBalanceAsync(_accountId));
            Assert.Equal(new[] { 1000m, 2000m }, second.Payments.Select(p => p.Amount).ToArray());
        }

        [Fact]
        public async Task RecordVendorPayment_InstallmentsUntilPaid_LastOneClosesTheBill()
        {
            var billId = await CreateBillAsync();
            await PayAsync(billId, PaymentBody(1000m));
            await PayAsync(billId, PaymentBody(1000m));

            var response = await PayAsync(billId, PaymentBody(2000m));

            var body = await ReadAsync(response);
            Assert.Equal("paid", body.Status);
            Assert.Equal(0m, body.Balance);
            Assert.Equal(3, body.Payments.Count);
            Assert.Equal(0m, await VendorBalanceAsync());
        }

        [Theory]
        [InlineData("Cash")]
        [InlineData("Card")]
        [InlineData("Transfer")]
        [InlineData("Check")]
        [InlineData("check")]
        public async Task RecordVendorPayment_EachAllowedMethod_Returns200(string method)
        {
            var billId = await CreateBillAsync();

            var response = await PayAsync(billId, PaymentBody(method: method));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // ---- RecordVendorPayment: 404 ----

        [Fact]
        public async Task RecordVendorPayment_UnknownBill_Returns404()
        {
            var response = await PayAsync(long.MaxValue, PaymentBody());

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        // ---- RecordVendorPayment: 400, nothing saved (AC 1, 2, 6) ----

        [Theory]
        [InlineData(0)]
        [InlineData(-5)]
        public async Task RecordVendorPayment_AmountNotPositive_Returns400AndSavesNothing(double amount)
        {
            var billId = await CreateBillAsync();

            var response = await PayAsync(billId, PaymentBody((decimal)amount));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            await AssertNothingSavedAsync(billId);
        }

        [Fact]
        public async Task RecordVendorPayment_AmountAboveBalance_Returns400WithErrorAndSavesNothing()
        {
            var billId = await CreateBillAsync();

            var response = await PayAsync(billId, PaymentBody(BillAmount + 0.01m));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var errors = (JArray)(await ReadJsonAsync(response)).GetValue("errors", StringComparison.OrdinalIgnoreCase)!;
            Assert.Contains("cannot exceed the AP balance", errors[0]!.ToString());
            await AssertNothingSavedAsync(billId);
        }

        [Fact]
        public async Task RecordVendorPayment_AmountAboveTheRemainingBalanceAfterAnInstallment_Returns400()
        {
            var billId = await CreateBillAsync();
            await PayAsync(billId, PaymentBody(3000m));

            var response = await PayAsync(billId, PaymentBody(1000.01m));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(1, await PaymentCountAsync(billId));
            Assert.Equal(3000m, await QueryAsync<decimal>("SELECT paid FROM ap_bill WHERE id = @Id", new { Id = billId }));
        }

        [Fact]
        public async Task RecordVendorPayment_PayingAnAlreadyPaidBill_Returns400WithAlreadyPaid()
        {
            var billId = await CreateBillAsync();
            await PayAsync(billId, PaymentBody(BillAmount));

            var response = await PayAsync(billId, PaymentBody(1m));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var errors = (JArray)(await ReadJsonAsync(response)).GetValue("errors", StringComparison.OrdinalIgnoreCase)!;
            Assert.Contains("already paid", errors[0]!.ToString());
            Assert.Equal(1, await PaymentCountAsync(billId));
        }

        [Fact]
        public async Task RecordVendorPayment_AmountAboveBookBalance_Returns400AndSavesNothing()
        {
            var billId = await CreateBillAsync();

            var response = await PayAsync(billId, PaymentBody(LowBalance + 0.01m, _lowAccountId));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var errors = (JArray)(await ReadJsonAsync(response)).GetValue("errors", StringComparison.OrdinalIgnoreCase)!;
            Assert.Contains("book balance", errors[0]!.ToString());
            await AssertNothingSavedAsync(billId);
        }

        [Fact]
        public async Task RecordVendorPayment_AmountEqualToBookBalance_Returns200AndEmptiesTheAccount()
        {
            var billId = await CreateBillAsync();

            var response = await PayAsync(billId, PaymentBody(LowBalance, _lowAccountId));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(0m, await BookBalanceAsync(_lowAccountId));
        }

        [Fact]
        public async Task RecordVendorPayment_InactiveAccount_Returns400AndSavesNothing()
        {
            var billId = await CreateBillAsync();

            var response = await PayAsync(billId, PaymentBody(accountId: _inactiveAccountId));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var errors = (JArray)(await ReadJsonAsync(response)).GetValue("errors", StringComparison.OrdinalIgnoreCase)!;
            Assert.Contains("must be active", errors[0]!.ToString());
            await AssertNothingSavedAsync(billId);
        }

        [Fact]
        public async Task RecordVendorPayment_UnknownAccount_Returns400AndSavesNothing()
        {
            var billId = await CreateBillAsync();

            var response = await PayAsync(billId, PaymentBody(accountId: long.MaxValue));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            await AssertNothingSavedAsync(billId);
        }

        [Fact]
        public async Task RecordVendorPayment_NoBankAccountInBody_Returns400AndSavesNothing()
        {
            var billId = await CreateBillAsync();

            var response = await PayAsync(billId, new { Payment_Date = "2026-11-08", Amount = 1500m, Payment_Method = "Cash" });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            await AssertNothingSavedAsync(billId);
        }

        [Fact]
        public async Task RecordVendorPayment_UnknownPaymentMethod_Returns400AndSavesNothing()
        {
            var billId = await CreateBillAsync();

            var response = await PayAsync(billId, PaymentBody(method: "bitcoin"));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            await AssertNothingSavedAsync(billId);
        }

        [Fact]
        public async Task RecordVendorPayment_NoPaymentMethod_Returns400AndSavesNothing()
        {
            var billId = await CreateBillAsync();

            var response = await PayAsync(billId, new { Payment_Date = "2026-11-08", Amount = 1500m, Bank_Account_Id = _accountId });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            await AssertNothingSavedAsync(billId);
        }

        [Fact]
        public async Task RecordVendorPayment_ReferenceOver100Characters_Returns400AndSavesNothing()
        {
            var billId = await CreateBillAsync();

            var response = await PayAsync(billId, PaymentBody(reference: new string('x', 101)));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            await AssertNothingSavedAsync(billId);
        }

        [Fact]
        public async Task RecordVendorPayment_NoBody_Returns400()
        {
            var billId = await CreateBillAsync();

            var response = await _testClient.PostAsync($"APBill/RecordVendorPayment/{billId}", Json(null));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task RecordVendorPayment_NonNumericId_IsNotRouted()
        {
            var response = await _testClient.PostAsync("APBill/RecordVendorPayment/abc", Json(PaymentBody()));

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        // ---- Routes ----

        [Fact]
        public async Task RecordVendorPayment_GetVerb_Returns405()
        {
            var billId = await CreateBillAsync();

            var response = await _testClient.GetAsync($"APBill/RecordVendorPayment/{billId}");

            Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        }

        [Fact]
        public async Task GetPaymentHistory_PostVerb_Returns405()
        {
            var billId = await CreateBillAsync();

            var response = await _testClient.PostAsync($"APBill/GetPaymentHistory/{billId}", Json(new { }));

            Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        }

        [Theory]
        [InlineData("PUT")]
        [InlineData("DELETE")]
        public async Task OtherVerbsOnTheBill_AreNotAllowed(string verb)
        {
            var billId = await CreateBillAsync();

            var response = await _testClient.SendAsync(new HttpRequestMessage(new HttpMethod(verb), $"APBill/RecordVendorPayment/{billId}"));

            Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        }

        [Fact]
        public async Task NoAddUpdateOrDeleteActions_ExistOnTheController()
        {
            var billId = await CreateBillAsync();

            foreach (var action in new[] { "AddApBill", "UpdateApBill", "DeleteApBill" })
            {
                var post = await _testClient.PostAsync($"APBill/{action}/{billId}", Json(new { }));
                Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
            }
        }

        // ---- GetPaymentHistory ----

        [Fact]
        public async Task GetPaymentHistory_BillWithoutPayments_Returns200WithEmptyPayments()
        {
            var billId = await CreateBillAsync();

            var response = await _testClient.GetAsync($"APBill/GetPaymentHistory/{billId}");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await ReadAsync(response);
            Assert.Equal(billId, body.Id);
            Assert.Equal(BillAmount, body.Balance);
            Assert.Equal("unpaid", body.Status);
            Assert.Equal(BillAmount, body.Vendor_Balance);
            Assert.Empty(body.Payments);
        }

        [Fact]
        public async Task GetPaymentHistory_AfterPayments_ListsEveryPaymentOrderedByDateThenNumber()
        {
            var billId = await CreateBillAsync();
            await PayAsync(billId, PaymentBody(1000m, date: "2026-12-01"));
            await PayAsync(billId, PaymentBody(500m, _secondAccountId, date: "2026-11-15"));
            await PayAsync(billId, PaymentBody(300m, date: "2026-11-15"));

            var body = await ReadAsync(await _testClient.GetAsync($"APBill/GetPaymentHistory/{billId}"));

            // Same date: the earlier payment number comes first.
            Assert.Equal(new[] { 500m, 300m, 1000m }, body.Payments.Select(p => p.Amount).ToArray());
            Assert.Equal(1800m, body.Paid);
            Assert.Equal(2200m, body.Balance);
            Assert.Equal(2200m, body.Vendor_Balance);
            Assert.All(body.Payments, p => Assert.True(p.Transaction_Id > 0));
        }

        [Fact]
        public async Task GetPaymentHistory_UnknownBill_Returns404()
        {
            var response = await _testClient.GetAsync($"APBill/GetPaymentHistory/{long.MaxValue}");

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        [Fact]
        public async Task GetPaymentHistory_NonNumericId_IsNotRouted()
        {
            var response = await _testClient.GetAsync("APBill/GetPaymentHistory/abc");

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        [Fact]
        public async Task GetPaymentHistory_ChangesNothing()
        {
            var billId = await CreateBillAsync();

            await _testClient.GetAsync($"APBill/GetPaymentHistory/{billId}");

            await AssertNothingSavedAsync(billId);
        }
    }
}
