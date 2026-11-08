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
    // POST /Receipt/RecordArPayment/{arInvoiceId} and GET /Receipt/GetPaymentHistory/{arInvoiceId} (Story 15)
    // against the test database. Each AR is created through Story 14's PostInvoice:
    // invoice total 18000 + 1000 + 200 - 700 = 18500, first payment 6000 -> AR paid 6000, balance 12500, Partial.
    public class ReceiptControllerTest : TestBase, IAsyncLifetime
    {
        private const decimal Total = 18500m;
        private const decimal FirstPayment = 6000m;
        private const decimal OpeningBalance = 500m;

        private readonly string _marker = $"itest-{Guid.NewGuid():N}".Substring(0, 12);
        private long _bankId;
        private long _accountId;
        private long _secondAccountId;
        private long _inactiveAccountId;
        private long _customerId;
        private long _salespersonId;
        private int _vehicleCount;

        public async Task InitializeAsync()
        {
            using var connection = _repositoryContext.GetDatabaseConnection();
            _bankId = await connection.ExecuteScalarAsync<long>(
                "INSERT INTO bank (name) VALUES (@Name) RETURNING id", new { Name = $"{_marker} Bank" });
            _accountId = await InsertAccountAsync("Operating", "active");
            _secondAccountId = await InsertAccountAsync("Savings", "active");
            _inactiveAccountId = await InsertAccountAsync("Closed", "inactive");

            _customerId = (await new CustomerRepository(_repositoryContext).AddCustomer(new CustomerCreateDto
            {
                First_Name = _marker, Last_Name = "Buyer", Email = $"{_marker}@example.com", Status = Status.Active
            })).Id;
            _salespersonId = (await new EmployeeRepository(_repositoryContext).AddEmployee(new EmployeeCreateDto
            {
                First_Name = _marker, Last_Name = "Seller", Department = "Sales", Commission_Rate = 5m, Status = Status.Active
            })).Id;
        }

        public async Task DisposeAsync()
        {
            using var connection = _repositoryContext.GetDatabaseConnection();
            var pattern = new { Pattern = _marker + "%", VinPattern = _marker.ToUpperInvariant() + "%", BankId = _bankId };
            const string markerVehicles =
                "SELECT id FROM vehicle WHERE vendor_id IN (SELECT id FROM vendor WHERE name LIKE @Pattern) OR vin LIKE @VinPattern";
            const string markerInvoices =
                "SELECT id FROM sales_invoice WHERE vehicle_id IN (" + markerVehicles + ") OR customer_id IN (SELECT id FROM customer WHERE first_name LIKE @Pattern)";
            const string markerAccounts = "SELECT id FROM bank_account WHERE bank_id = @BankId";

            await connection.ExecuteAsync($"DELETE FROM bank_receipt WHERE bank_account_id IN ({markerAccounts})", pattern);
            await connection.ExecuteAsync($"DELETE FROM bank_statement_line WHERE bank_account_id IN ({markerAccounts})", pattern);
            await connection.ExecuteAsync($@"DELETE FROM ""transaction"" WHERE bank_account_id IN ({markerAccounts})", pattern);
            await connection.ExecuteAsync($"DELETE FROM ar_invoice WHERE invoice_id IN ({markerInvoices})", pattern);
            await connection.ExecuteAsync($"UPDATE vehicle SET invoice_id = NULL WHERE id IN ({markerVehicles})", pattern);
            await connection.ExecuteAsync($"DELETE FROM sales_invoice WHERE id IN ({markerInvoices})", pattern);
            await connection.ExecuteAsync($"DELETE FROM vehicle WHERE id IN ({markerVehicles})", pattern);
            await connection.ExecuteAsync("DELETE FROM vendor WHERE name LIKE @Pattern", pattern);
            await connection.ExecuteAsync("DELETE FROM customer WHERE first_name LIKE @Pattern", pattern);
            await connection.ExecuteAsync("DELETE FROM employee WHERE first_name LIKE @Pattern", pattern);
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

        // A posted sale with a 6000 down payment: returns the AR invoice id and its sales invoice id.
        private async Task<(long ArId, long InvoiceId)> CreateOpenArAsync()
        {
            var vendor = await new VendorRepository(_repositoryContext).AddVendor(new VendorCreateDto
            {
                Name = $"{_marker} Vendor", Type = VendorType.Vehicles, Status = Status.Active
            });
            var vehicle = await new VehicleRepository(_repositoryContext).AddVehicle(new VehicleCreateDto
            {
                Vendor_Id = vendor.Id,
                Vin = $"{_marker}{++_vehicleCount}".ToUpperInvariant().PadRight(17, '0').Substring(0, 17),
                Make = "Honda", Model = "Civic",
                Purchase_Price = 15000m, Additional_Costs = 250m, Selling_Price = 18000m
            });
            var draft = await new SalesInvoiceRepository(_repositoryContext).AddSalesInvoice(new SalesInvoiceCreateDto
            {
                Customer_Id = _customerId, Salesperson_Id = _salespersonId, Vehicle_Id = vehicle.Id,
                Invoice_Date = new DateTime(2026, 5, 1), Due_Date = new DateTime(2026, 5, 31),
                Tax = 1000m, Fees = 200m, Discount = 700m
            });

            var response = await _testClient.PostAsync($"SalesInvoice/PostInvoice?id={draft.Id}", Json(new
            {
                paymentCondition = "partialPayment", amountReceived = FirstPayment, paymentMethod = "transfer",
                reference = "DOWN-1", bankAccountId = _accountId, receiptDate = "2026-11-01"
            }));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var posted = JsonConvert.DeserializeObject<SalesInvoiceResponseDto>(await response.Content.ReadAsStringAsync())!;
            return (posted.Ar_Invoice_Id!.Value, draft.Id);
        }

        private static StringContent Json(object? body) =>
            new(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");

        private Task<HttpResponseMessage> PayAsync(long arId, object body) =>
            _testClient.PostAsync($"Receipt/RecordArPayment/{arId}", Json(body));

        private object PaymentBody(decimal amount = 4000m, long? accountId = null, string method = "check",
            string? reference = "CHK-1001", string date = "2026-11-08") => new
        {
            receiptDate = date, amount, paymentMethod = method, reference, bankAccountId = accountId ?? _accountId
        };

        private static async Task<ArInvoiceResponseDto> ReadAsync(HttpResponseMessage response) =>
            JsonConvert.DeserializeObject<ArInvoiceResponseDto>(await response.Content.ReadAsStringAsync())!;

        private static async Task<JObject> ReadJsonAsync(HttpResponseMessage response) =>
            JObject.Parse(await response.Content.ReadAsStringAsync());

        private async Task<T> QueryAsync<T>(string sql, object param)
        {
            using var connection = _repositoryContext.GetDatabaseConnection();
            return await connection.ExecuteScalarAsync<T>(sql, param) ?? default!;
        }

        private Task<decimal> BookBalanceAsync(long accountId) =>
            QueryAsync<decimal>("SELECT book_balance FROM bank_account WHERE id = @Id", new { Id = accountId });

        private Task<int> ReceiptCountAsync(long arId) =>
            QueryAsync<int>("SELECT COUNT(*) FROM bank_receipt WHERE ar_invoice_id = @Id", new { Id = arId });

        private async Task AssertNothingSavedAsync(long arId, long invoiceId)
        {
            Assert.Equal(1, await ReceiptCountAsync(arId));
            Assert.Equal(FirstPayment, await QueryAsync<decimal>("SELECT paid FROM ar_invoice WHERE id = @Id", new { Id = arId }));
            Assert.Equal(Total - FirstPayment, await QueryAsync<decimal>("SELECT balance FROM ar_invoice WHERE id = @Id", new { Id = arId }));
            Assert.Equal(FirstPayment, await QueryAsync<decimal>("SELECT paid FROM sales_invoice WHERE id = @Id", new { Id = invoiceId }));
            Assert.Equal("partial", await QueryAsync<string>("SELECT payment_status FROM sales_invoice WHERE id = @Id", new { Id = invoiceId }));
            Assert.Equal(OpeningBalance + FirstPayment, await BookBalanceAsync(_accountId));
            Assert.Equal(OpeningBalance, await BookBalanceAsync(_secondAccountId));
            Assert.Equal(OpeningBalance, await BookBalanceAsync(_inactiveAccountId));
        }

        // ---- RecordArPayment: 200 (AC 3, 4, 5, 7) ----

        [Fact]
        public async Task RecordArPayment_Partial_Returns200WithUpdatedArAndPaymentHistory()
        {
            var (arId, _) = await CreateOpenArAsync();

            var response = await PayAsync(arId, PaymentBody(4000m));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await ReadAsync(response);
            Assert.Equal(arId, body.Id);
            Assert.Equal(Total, body.Amount);
            Assert.Equal(10000m, body.Paid);
            Assert.Equal(8500m, body.Balance);
            Assert.Equal(PaymentStatus.Partial, body.Status);
            Assert.Equal(2, body.Payments.Count);
            Assert.Equal(new[] { FirstPayment, 4000m }, body.Payments.Select(p => p.Amount).ToArray());
        }

        [Fact]
        public async Task RecordArPayment_FullBalance_Returns200WithPaidStatusAndZeroBalance()
        {
            var (arId, invoiceId) = await CreateOpenArAsync();

            var response = await PayAsync(arId, PaymentBody(Total - FirstPayment));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await ReadAsync(response);
            Assert.Equal(Total, body.Paid);
            Assert.Equal(0m, body.Balance);
            Assert.Equal(PaymentStatus.Paid, body.Status);
            Assert.Equal("paid", await QueryAsync<string>("SELECT status FROM ar_invoice WHERE id = @Id", new { Id = arId }));
            Assert.Equal("paid", await QueryAsync<string>("SELECT payment_status FROM sales_invoice WHERE id = @Id", new { Id = invoiceId }));
        }

        [Fact]
        public async Task RecordArPayment_Returns200WithTheDocumentedFields()
        {
            var (arId, _) = await CreateOpenArAsync();

            var json = await ReadJsonAsync(await PayAsync(arId, PaymentBody()));

            foreach (var name in new[] { "id", "number", "invoiceNumber", "customerName", "dueDate", "amount", "paid", "balance", "status", "payments" })
                Assert.NotNull(json.GetValue(name, StringComparison.OrdinalIgnoreCase));
            Assert.Equal("partial", json.GetValue("status", StringComparison.OrdinalIgnoreCase)!.ToString());
            var payment = (JObject)((JArray)json.GetValue("payments", StringComparison.OrdinalIgnoreCase)!)[1];
            foreach (var name in new[] { "id", "receiptNumber", "receiptDate", "amount", "paymentMethod", "reference", "bankAccountName", "transactionId" })
                Assert.NotNull(payment.GetValue(name, StringComparison.OrdinalIgnoreCase));
            Assert.Equal("check", payment.GetValue("paymentMethod", StringComparison.OrdinalIgnoreCase)!.ToString());
        }

        [Fact]
        public async Task RecordArPayment_UpdatesArAndSalesInvoiceTogether()
        {
            var (arId, invoiceId) = await CreateOpenArAsync();

            await PayAsync(arId, PaymentBody(4000m));

            Assert.Equal(10000m, await QueryAsync<decimal>("SELECT paid FROM ar_invoice WHERE id = @Id", new { Id = arId }));
            Assert.Equal(8500m, await QueryAsync<decimal>("SELECT balance FROM ar_invoice WHERE id = @Id", new { Id = arId }));
            Assert.Equal("partial", await QueryAsync<string>("SELECT status FROM ar_invoice WHERE id = @Id", new { Id = arId }));
            Assert.Equal(10000m, await QueryAsync<decimal>("SELECT paid FROM sales_invoice WHERE id = @Id", new { Id = invoiceId }));
            Assert.Equal(8500m, await QueryAsync<decimal>("SELECT balance FROM sales_invoice WHERE id = @Id", new { Id = invoiceId }));
            Assert.Equal("partial", await QueryAsync<string>("SELECT payment_status FROM sales_invoice WHERE id = @Id", new { Id = invoiceId }));
        }

        [Fact]
        public async Task RecordArPayment_RaisesTheSelectedAccountsBookBalanceOnly()
        {
            var (arId, _) = await CreateOpenArAsync();

            await PayAsync(arId, PaymentBody(4000m, _secondAccountId));

            Assert.Equal(OpeningBalance + FirstPayment, await BookBalanceAsync(_accountId));
            Assert.Equal(OpeningBalance + 4000m, await BookBalanceAsync(_secondAccountId));
        }

        [Fact]
        public async Task RecordArPayment_CreatesReceiptTransactionAndStatementLine()
        {
            var (arId, invoiceId) = await CreateOpenArAsync();

            await PayAsync(arId, PaymentBody(4000m, _secondAccountId, "card", "POS-5", "2026-11-08"));

            var receipt = await QueryAsync<long>(
                @"SELECT id FROM bank_receipt WHERE ar_invoice_id = @Id AND amount = 4000", new { Id = arId });
            Assert.True(receipt > 0);
            Assert.Equal(1, await QueryAsync<int>(
                @"SELECT COUNT(*) FROM bank_receipt r
                  JOIN ""transaction"" t ON t.id = r.transaction_id AND t.bank_account_id = @AccountId AND t.amount = 4000
                  JOIN bank_statement_line s ON s.transaction_id = t.id AND s.deposit = 4000
                  WHERE r.id = @ReceiptId AND r.bank_account_id = @AccountId AND r.sales_invoice_id = @InvoiceId
                    AND r.customer_id = @CustomerId AND r.payment_method = 'Card' AND r.reference = 'POS-5'",
                new { ReceiptId = receipt, AccountId = _secondAccountId, InvoiceId = invoiceId, CustomerId = _customerId }));
        }

        [Fact]
        public async Task RecordArPayment_TwoPayments_CreateTwoSeparateReceiptsAndKeepTheEarlierOnesUnchanged()
        {
            var (arId, _) = await CreateOpenArAsync();
            var firstBefore = await QueryAsync<long>("SELECT id FROM bank_receipt WHERE ar_invoice_id = @Id", new { Id = arId });

            await PayAsync(arId, PaymentBody(1000m));
            await PayAsync(arId, PaymentBody(2000m));

            Assert.Equal(3, await ReceiptCountAsync(arId));
            Assert.Equal(3, await QueryAsync<int>(
                "SELECT COUNT(DISTINCT transaction_id) FROM bank_receipt WHERE ar_invoice_id = @Id", new { Id = arId }));
            Assert.Equal(3, await QueryAsync<int>(
                @"SELECT COUNT(*) FROM bank_statement_line WHERE transaction_id IN (SELECT transaction_id FROM bank_receipt WHERE ar_invoice_id = @Id)",
                new { Id = arId }));
            Assert.Equal(FirstPayment, await QueryAsync<decimal>("SELECT amount FROM bank_receipt WHERE id = @Id", new { Id = firstBefore }));
            Assert.Equal("DOWN-1", await QueryAsync<string>("SELECT reference FROM bank_receipt WHERE id = @Id", new { Id = firstBefore }));
            Assert.Equal(OpeningBalance + FirstPayment + 3000m, await BookBalanceAsync(_accountId));
        }

        [Theory]
        [InlineData("cash")]
        [InlineData("card")]
        [InlineData("transfer")]
        [InlineData("check")]
        public async Task RecordArPayment_EachAllowedMethod_Returns200(string method)
        {
            var (arId, _) = await CreateOpenArAsync();

            var response = await PayAsync(arId, PaymentBody(method: method));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // ---- RecordArPayment: 404 ----

        [Fact]
        public async Task RecordArPayment_UnknownAr_Returns404()
        {
            var response = await PayAsync(long.MaxValue, PaymentBody());

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        // ---- RecordArPayment: 400, nothing saved (AC 1, 2, 6) ----

        [Theory]
        [InlineData(0)]
        [InlineData(-5)]
        public async Task RecordArPayment_AmountNotPositive_Returns400WithErrorsAndSavesNothing(double amount)
        {
            var (arId, invoiceId) = await CreateOpenArAsync();

            var response = await PayAsync(arId, PaymentBody((decimal)amount));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var errors = (JArray)(await ReadJsonAsync(response)).GetValue("errors", StringComparison.OrdinalIgnoreCase)!;
            Assert.Contains("greater than zero", errors[0]!.ToString());
            await AssertNothingSavedAsync(arId, invoiceId);
        }

        [Fact]
        public async Task RecordArPayment_AmountAboveBalance_Returns400AndSavesNothing()
        {
            var (arId, invoiceId) = await CreateOpenArAsync();

            var response = await PayAsync(arId, PaymentBody(Total - FirstPayment + 0.01m));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var errors = (JArray)(await ReadJsonAsync(response)).GetValue("errors", StringComparison.OrdinalIgnoreCase)!;
            Assert.Contains("cannot exceed the AR balance", errors[0]!.ToString());
            await AssertNothingSavedAsync(arId, invoiceId);
        }

        [Fact]
        public async Task RecordArPayment_PayingAnAlreadyPaidAr_Returns400()
        {
            var (arId, _) = await CreateOpenArAsync();
            await PayAsync(arId, PaymentBody(Total - FirstPayment));

            var response = await PayAsync(arId, PaymentBody(1m));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(2, await ReceiptCountAsync(arId));
        }

        [Fact]
        public async Task RecordArPayment_InactiveAccount_Returns400AndSavesNothing()
        {
            var (arId, invoiceId) = await CreateOpenArAsync();

            var response = await PayAsync(arId, PaymentBody(accountId: _inactiveAccountId));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var errors = (JArray)(await ReadJsonAsync(response)).GetValue("errors", StringComparison.OrdinalIgnoreCase)!;
            Assert.Contains("must be active", errors[0]!.ToString());
            await AssertNothingSavedAsync(arId, invoiceId);
        }

        [Fact]
        public async Task RecordArPayment_UnknownAccount_Returns400AndSavesNothing()
        {
            var (arId, invoiceId) = await CreateOpenArAsync();

            var response = await PayAsync(arId, PaymentBody(accountId: long.MaxValue));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            await AssertNothingSavedAsync(arId, invoiceId);
        }

        [Fact]
        public async Task RecordArPayment_NoBankAccountInBody_Returns400AndSavesNothing()
        {
            var (arId, invoiceId) = await CreateOpenArAsync();

            var response = await PayAsync(arId, new { receiptDate = "2026-11-08", amount = 4000m, paymentMethod = "cash" });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            await AssertNothingSavedAsync(arId, invoiceId);
        }

        [Fact]
        public async Task RecordArPayment_UnknownPaymentMethod_Returns400AndSavesNothing()
        {
            var (arId, invoiceId) = await CreateOpenArAsync();

            var response = await PayAsync(arId, PaymentBody(method: "bitcoin"));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            await AssertNothingSavedAsync(arId, invoiceId);
        }

        [Fact]
        public async Task RecordArPayment_ReferenceOver100Characters_Returns400AndSavesNothing()
        {
            var (arId, invoiceId) = await CreateOpenArAsync();

            var response = await PayAsync(arId, PaymentBody(reference: new string('x', 101)));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            await AssertNothingSavedAsync(arId, invoiceId);
        }

        [Fact]
        public async Task RecordArPayment_NoBody_Returns400()
        {
            var (arId, _) = await CreateOpenArAsync();

            var response = await _testClient.PostAsync($"Receipt/RecordArPayment/{arId}", Json(null));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task RecordArPayment_NonNumericId_IsNotRouted()
        {
            var response = await _testClient.PostAsync("Receipt/RecordArPayment/abc", Json(PaymentBody()));

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        // ---- RecordArPayment: 409 ----

        [Fact]
        public async Task RecordArPayment_SalesInvoiceNoLongerPosted_Returns409AndRollsBackTheArUpdate()
        {
            var (arId, invoiceId) = await CreateOpenArAsync();
            using (var connection = _repositoryContext.GetDatabaseConnection())
            {
                // Simulates the sales invoice changing between the read and the save.
                await connection.ExecuteAsync(
                    "UPDATE sales_invoice SET lifecycle = 'cancelled', cancel_reason = 'test' WHERE id = @Id", new { Id = invoiceId });
            }

            var response = await PayAsync(arId, PaymentBody(4000m));

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal(FirstPayment, await QueryAsync<decimal>("SELECT paid FROM ar_invoice WHERE id = @Id", new { Id = arId }));
            Assert.Equal(1, await ReceiptCountAsync(arId));
            Assert.Equal(OpeningBalance + FirstPayment, await BookBalanceAsync(_accountId));
        }

        // ---- Routes ----

        [Fact]
        public async Task RecordArPayment_GetVerb_Returns405()
        {
            var (arId, _) = await CreateOpenArAsync();

            var response = await _testClient.GetAsync($"Receipt/RecordArPayment/{arId}");

            Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        }

        [Fact]
        public async Task GetPaymentHistory_PostVerb_Returns405()
        {
            var (arId, _) = await CreateOpenArAsync();

            var response = await _testClient.PostAsync($"Receipt/GetPaymentHistory/{arId}", Json(new { }));

            Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        }

        // ---- GetPaymentHistory ----

        [Fact]
        public async Task GetPaymentHistory_Existing_Returns200WithArAndItsPayment()
        {
            var (arId, _) = await CreateOpenArAsync();

            var response = await _testClient.GetAsync($"Receipt/GetPaymentHistory/{arId}");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await ReadAsync(response);
            Assert.Equal(arId, body.Id);
            Assert.Equal($"AR-{arId:D6}", body.Number);
            Assert.Equal($"{_marker} Buyer", body.CustomerName);
            Assert.Equal(FirstPayment, body.Paid);
            Assert.Equal(Total - FirstPayment, body.Balance);
            var payment = Assert.Single(body.Payments);
            Assert.Equal(FirstPayment, payment.Amount);
            Assert.Equal(PaymentMethod.Transfer, payment.PaymentMethod);
            Assert.Equal("DOWN-1", payment.Reference);
            Assert.Equal($"{_marker} Operating", payment.BankAccountName);
        }

        [Fact]
        public async Task GetPaymentHistory_AfterLaterPayments_ListsEveryReceiptOldestFirst()
        {
            var (arId, _) = await CreateOpenArAsync();
            await PayAsync(arId, PaymentBody(1000m, date: "2026-12-01"));
            await PayAsync(arId, PaymentBody(2000m, _secondAccountId, date: "2026-11-15"));

            var body = await ReadAsync(await _testClient.GetAsync($"Receipt/GetPaymentHistory/{arId}"));

            Assert.Equal(new[] { FirstPayment, 2000m, 1000m }, body.Payments.Select(p => p.Amount).ToArray());
            Assert.Equal($"{_marker} Savings", body.Payments[1].BankAccountName);
            Assert.Equal(9000m, body.Paid);
        }

        [Fact]
        public async Task GetPaymentHistory_UnknownAr_Returns404()
        {
            var response = await _testClient.GetAsync($"Receipt/GetPaymentHistory/{long.MaxValue}");

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        [Fact]
        public async Task GetPaymentHistory_NonNumericId_IsNotRouted()
        {
            var response = await _testClient.GetAsync("Receipt/GetPaymentHistory/abc");

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        [Fact]
        public async Task GetPaymentHistory_ChangesNothing()
        {
            var (arId, invoiceId) = await CreateOpenArAsync();

            await _testClient.GetAsync($"Receipt/GetPaymentHistory/{arId}");

            await AssertNothingSavedAsync(arId, invoiceId);
        }
    }
}
