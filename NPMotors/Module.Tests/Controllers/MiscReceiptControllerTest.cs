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
    // POST /Receipt/RecordMiscReceipt (Story 16) against the test database.
    // Accounts start with book balance 500. A receipt of 750.50 therefore gives book balance 1250.50.
    public class MiscReceiptControllerTest : TestBase, IAsyncLifetime
    {
        private const decimal OpeningBalance = 500m;
        private const decimal Amount = 750.50m;

        private readonly string _marker = $"itest-{Guid.NewGuid():N}".Substring(0, 12);
        private long _bankId;
        private long _accountId;
        private long _secondAccountId;
        private long _inactiveAccountId;
        private long _customerId;
        private long _salespersonId;

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

        // A posted sale with a 6000 down payment, so an AR invoice, a sales invoice and customer totals exist.
        private async Task<(long ArId, long InvoiceId)> CreateOpenArAsync()
        {
            var vendor = await new VendorRepository(_repositoryContext).AddVendor(new VendorCreateDto
            {
                Name = $"{_marker} Vendor", Type = VendorType.Vehicles, Status = Status.Active
            });
            var vehicle = await new VehicleRepository(_repositoryContext).AddVehicle(new VehicleCreateDto
            {
                Vendor_Id = vendor.Id,
                Vin = $"{_marker}1".ToUpperInvariant().PadRight(17, '0').Substring(0, 17),
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
                paymentCondition = "partialPayment", amountReceived = 6000m, paymentMethod = "transfer",
                reference = "DOWN-1", bankAccountId = _accountId, receiptDate = "2026-11-01"
            }));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var posted = JsonConvert.DeserializeObject<SalesInvoiceResponseDto>(await response.Content.ReadAsStringAsync())!;
            return (posted.Ar_Invoice_Id!.Value, draft.Id);
        }

        private static StringContent Json(object? body) =>
            new(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");

        private Task<HttpResponseMessage> RecordAsync(object body) =>
            _testClient.PostAsync("Receipt/RecordMiscReceipt", Json(body));

        private object ReceiptBody(string payer = "Acme Corp", string date = "2026-11-08", decimal amount = Amount,
            string method = "check", string? reference = "CHK-55", long? accountId = null,
            string description = "Scrap metal sale") => new
        {
            payer, receiptDate = date, amount, paymentMethod = method, reference,
            bankAccountId = accountId ?? _accountId, description
        };

        private static async Task<BankReceiptResponseDto> ReadAsync(HttpResponseMessage response) =>
            JsonConvert.DeserializeObject<BankReceiptResponseDto>(await response.Content.ReadAsStringAsync())!;

        private static async Task<JObject> ReadJsonAsync(HttpResponseMessage response) =>
            JObject.Parse(await response.Content.ReadAsStringAsync());

        private async Task<T> QueryAsync<T>(string sql, object param)
        {
            using var connection = _repositoryContext.GetDatabaseConnection();
            return await connection.ExecuteScalarAsync<T>(sql, param) ?? default!;
        }

        private Task<decimal> BookBalanceAsync(long accountId) =>
            QueryAsync<decimal>("SELECT book_balance FROM bank_account WHERE id = @Id", new { Id = accountId });

        private Task<int> ReceiptCountAsync(long accountId) =>
            QueryAsync<int>("SELECT COUNT(*) FROM bank_receipt WHERE bank_account_id = @Id", new { Id = accountId });

        private Task<int> TransactionCountAsync(long accountId) =>
            QueryAsync<int>(@"SELECT COUNT(*) FROM ""transaction"" WHERE bank_account_id = @Id", new { Id = accountId });

        private Task<int> StatementLineCountAsync(long accountId) =>
            QueryAsync<int>("SELECT COUNT(*) FROM bank_statement_line WHERE bank_account_id = @Id", new { Id = accountId });

        // A rejected request must save nothing on any of the three accounts.
        private async Task AssertNothingSavedAsync()
        {
            foreach (var id in new[] { _accountId, _secondAccountId, _inactiveAccountId })
            {
                Assert.Equal(0, await ReceiptCountAsync(id));
                Assert.Equal(0, await TransactionCountAsync(id));
                Assert.Equal(0, await StatementLineCountAsync(id));
                Assert.Equal(OpeningBalance, await BookBalanceAsync(id));
            }
        }

        // ---- 201 (AC 4) ----

        [Fact]
        public async Task RecordMiscReceipt_Valid_Returns201WithTheNewReceipt()
        {
            var response = await RecordAsync(ReceiptBody());

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var body = await ReadAsync(response);
            Assert.True(body.Id > 0);
            Assert.Equal($"RCT-{DateTime.UtcNow.Year}-{body.Id:D6}", body.ReceiptNumber);
            Assert.Equal(new DateTime(2026, 11, 8), body.ReceiptDate.Date);
            Assert.Equal("Acme Corp", body.Payer);
            Assert.Equal(Amount, body.Amount);
            Assert.Equal(PaymentMethod.Check, body.PaymentMethod);
            Assert.Equal("CHK-55", body.Reference);
            Assert.Equal("Scrap metal sale", body.Description);
            Assert.EndsWith("Operating", body.BankAccountName);
            Assert.True(body.TransactionId > 0);
        }

        [Fact]
        public async Task RecordMiscReceipt_Valid_Returns201WithTheDocumentedFields()
        {
            var json = await ReadJsonAsync(await RecordAsync(ReceiptBody()));

            foreach (var name in new[] { "id", "receiptNumber", "receiptDate", "payer", "amount", "paymentMethod",
                                         "reference", "description", "bankAccountName", "transactionId" })
                Assert.NotNull(json.GetValue(name, StringComparison.OrdinalIgnoreCase));
            Assert.Equal("check", json.GetValue("paymentMethod", StringComparison.OrdinalIgnoreCase)!.ToString());
        }

        [Fact]
        public async Task RecordMiscReceipt_ReferenceOmitted_Returns201()
        {
            var response = await RecordAsync(new
            {
                payer = "Acme Corp", receiptDate = "2026-11-08", amount = Amount, paymentMethod = "cash",
                bankAccountId = _accountId, description = "Scrap metal sale"
            });

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            Assert.Null((await ReadAsync(response)).Reference);
        }

        [Fact]
        public async Task RecordMiscReceipt_Valid_CreatesOneTransactionOneStatementLineAndRaisesBookBalance()
        {
            var body = await ReadAsync(await RecordAsync(ReceiptBody()));

            Assert.Equal(1, await TransactionCountAsync(_accountId));
            Assert.Equal(1, await StatementLineCountAsync(_accountId));
            Assert.Equal(1, await ReceiptCountAsync(_accountId));
            Assert.Equal(OpeningBalance + Amount, await BookBalanceAsync(_accountId));
            // One "in" transaction: type BankReceipt, party = payer, posted.
            Assert.Equal(1, await QueryAsync<int>(
                @"SELECT COUNT(*) FROM ""transaction""
                  WHERE id = @TransactionId AND bank_account_id = @AccountId AND direction = 'in'::direction_enum
                    AND amount = @Amount AND party = 'Acme Corp' AND type = 'BankReceipt' AND status = 'posted'
                    AND description = 'Scrap metal sale'",
                new { body.TransactionId, AccountId = _accountId, Amount }));
            // One statement line: deposit = amount, running balance = opening + amount.
            Assert.Equal(1, await QueryAsync<int>(
                @"SELECT COUNT(*) FROM bank_statement_line
                  WHERE transaction_id = @TransactionId AND deposit = @Amount AND withdrawal = 0
                    AND party = 'Acme Corp' AND running_balance = @Running
                    AND match_status = 'Matched' AND statement_status = 'Cleared'",
                new { body.TransactionId, Amount, Running = OpeningBalance + Amount }));
        }

        [Fact]
        public async Task RecordMiscReceipt_Valid_StoresReceiptRowWithPayerAndNoCustomerInvoiceOrArLink()
        {
            var body = await ReadAsync(await RecordAsync(ReceiptBody(method: "card", reference: "POS-5")));

            Assert.Equal(1, await QueryAsync<int>(
                @"SELECT COUNT(*) FROM bank_receipt
                  WHERE id = @Id AND transaction_id = @TransactionId AND bank_account_id = @AccountId
                    AND customer_id IS NULL AND sales_invoice_id IS NULL AND ar_invoice_id IS NULL
                    AND payer = 'Acme Corp' AND description = 'Scrap metal sale' AND amount = @Amount
                    AND payment_method = 'Card' AND reference = 'POS-5' AND receipt_date = DATE '2026-11-08'
                    AND status = 'Posted' AND created_by = 'system'",
                new { body.Id, body.TransactionId, AccountId = _accountId, Amount }));
        }

        [Fact]
        public async Task RecordMiscReceipt_RaisesTheSelectedAccountsBookBalanceOnly()
        {
            await RecordAsync(ReceiptBody(accountId: _secondAccountId));

            Assert.Equal(OpeningBalance, await BookBalanceAsync(_accountId));
            Assert.Equal(OpeningBalance + Amount, await BookBalanceAsync(_secondAccountId));
            Assert.Equal(OpeningBalance, await BookBalanceAsync(_inactiveAccountId));
        }

        // ---- AC 3: no customer, invoice or AR balance changes ----

        [Fact]
        public async Task RecordMiscReceipt_DoesNotChangeAnyCustomerInvoiceOrArBalance()
        {
            var (arId, invoiceId) = await CreateOpenArAsync();
            var customerBefore = await QueryAsync<string>(
                "SELECT balance::text || '/' || total_purchases::text FROM customer WHERE id = @Id", new { Id = _customerId });
            var arBefore = await QueryAsync<string>(
                "SELECT paid::text || '/' || balance::text || '/' || status FROM ar_invoice WHERE id = @Id", new { Id = arId });
            var invoiceBefore = await QueryAsync<string>(
                "SELECT paid::text || '/' || balance::text || '/' || payment_status FROM sales_invoice WHERE id = @Id", new { Id = invoiceId });
            var arReceiptsBefore = await QueryAsync<int>(
                "SELECT COUNT(*) FROM bank_receipt WHERE ar_invoice_id = @Id", new { Id = arId });

            var response = await RecordAsync(ReceiptBody(amount: 4000m));

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            Assert.Equal(customerBefore, await QueryAsync<string>(
                "SELECT balance::text || '/' || total_purchases::text FROM customer WHERE id = @Id", new { Id = _customerId }));
            Assert.Equal(arBefore, await QueryAsync<string>(
                "SELECT paid::text || '/' || balance::text || '/' || status FROM ar_invoice WHERE id = @Id", new { Id = arId }));
            Assert.Equal(invoiceBefore, await QueryAsync<string>(
                "SELECT paid::text || '/' || balance::text || '/' || payment_status FROM sales_invoice WHERE id = @Id", new { Id = invoiceId }));
            Assert.Equal(arReceiptsBefore, await QueryAsync<int>(
                "SELECT COUNT(*) FROM bank_receipt WHERE ar_invoice_id = @Id", new { Id = arId }));
        }

        // ---- AC 6: each receipt is separate ----

        [Fact]
        public async Task RecordMiscReceipt_TwoReceipts_CreateTwoSeparateRowsAndKeepTheEarlierOneUnchanged()
        {
            var first = await ReadAsync(await RecordAsync(ReceiptBody(payer: "Acme Corp", amount: 100m, reference: "R-1")));
            var second = await ReadAsync(await RecordAsync(ReceiptBody(payer: "Bolt Ltd", amount: 200m, reference: "R-2")));

            Assert.NotEqual(first.Id, second.Id);
            Assert.NotEqual(first.TransactionId, second.TransactionId);
            Assert.Equal(2, await ReceiptCountAsync(_accountId));
            Assert.Equal(2, await TransactionCountAsync(_accountId));
            Assert.Equal(2, await StatementLineCountAsync(_accountId));
            Assert.Equal(OpeningBalance + 300m, await BookBalanceAsync(_accountId));
            // The earlier receipt is exactly as first saved.
            Assert.Equal(1, await QueryAsync<int>(
                @"SELECT COUNT(*) FROM bank_receipt
                  WHERE id = @Id AND payer = 'Acme Corp' AND amount = 100 AND reference = 'R-1' AND transaction_id = @TransactionId",
                new { first.Id, first.TransactionId }));
        }

        [Fact]
        public async Task RecordMiscReceipt_SameBodyTwice_CreatesTwoReceipts()
        {
            await RecordAsync(ReceiptBody());
            await RecordAsync(ReceiptBody());

            Assert.Equal(2, await ReceiptCountAsync(_accountId));
            Assert.Equal(OpeningBalance + 2 * Amount, await BookBalanceAsync(_accountId));
        }

        [Fact]
        public async Task RecordMiscReceipt_StatementRunningBalanceBuildsOnThePreviousLine()
        {
            await RecordAsync(ReceiptBody(amount: 100m));
            var second = await ReadAsync(await RecordAsync(ReceiptBody(amount: 200m)));

            Assert.Equal(OpeningBalance + 300m, await QueryAsync<decimal>(
                "SELECT running_balance FROM bank_statement_line WHERE transaction_id = @Id", new { Id = second.TransactionId }));
        }

        // ---- 400, nothing saved (AC 1, 2, 5) ----

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public async Task RecordMiscReceipt_PayerMissing_Returns400AndSavesNothing(string payer)
        {
            var response = await RecordAsync(ReceiptBody(payer: payer));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var errors = (JArray)(await ReadJsonAsync(response)).GetValue("errors", StringComparison.OrdinalIgnoreCase)!;
            Assert.Contains("Payer is required", errors[0]!.ToString());
            await AssertNothingSavedAsync();
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public async Task RecordMiscReceipt_DescriptionMissing_Returns400AndSavesNothing(string description)
        {
            var response = await RecordAsync(ReceiptBody(description: description));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var errors = (JArray)(await ReadJsonAsync(response)).GetValue("errors", StringComparison.OrdinalIgnoreCase)!;
            Assert.Contains("Description is required", errors[0]!.ToString());
            await AssertNothingSavedAsync();
        }

        [Fact]
        public async Task RecordMiscReceipt_DateMissing_Returns400AndSavesNothing()
        {
            var response = await RecordAsync(new
            {
                payer = "Acme Corp", amount = Amount, paymentMethod = "check",
                bankAccountId = _accountId, description = "Scrap metal sale"
            });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var errors = (JArray)(await ReadJsonAsync(response)).GetValue("errors", StringComparison.OrdinalIgnoreCase)!;
            Assert.Contains("Receipt date is required", errors[0]!.ToString());
            await AssertNothingSavedAsync();
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-5)]
        public async Task RecordMiscReceipt_AmountNotPositive_Returns400AndSavesNothing(double amount)
        {
            var response = await RecordAsync(ReceiptBody(amount: (decimal)amount));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var errors = (JArray)(await ReadJsonAsync(response)).GetValue("errors", StringComparison.OrdinalIgnoreCase)!;
            Assert.Contains("greater than zero", errors[0]!.ToString());
            await AssertNothingSavedAsync();
        }

        [Fact]
        public async Task RecordMiscReceipt_AmountOmitted_Returns400AndSavesNothing()
        {
            var response = await RecordAsync(new
            {
                payer = "Acme Corp", receiptDate = "2026-11-08", paymentMethod = "check",
                bankAccountId = _accountId, description = "Scrap metal sale"
            });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            await AssertNothingSavedAsync();
        }

        [Fact]
        public async Task RecordMiscReceipt_UnknownPaymentMethod_Returns400AndSavesNothing()
        {
            var response = await RecordAsync(ReceiptBody(method: "bitcoin"));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            await AssertNothingSavedAsync();
        }

        [Fact]
        public async Task RecordMiscReceipt_BankAccountOmitted_Returns400AndSavesNothing()
        {
            var response = await RecordAsync(new
            {
                payer = "Acme Corp", receiptDate = "2026-11-08", amount = Amount, paymentMethod = "check",
                description = "Scrap metal sale"
            });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            await AssertNothingSavedAsync();
        }

        [Fact]
        public async Task RecordMiscReceipt_UnknownBankAccount_Returns400AndSavesNothing()
        {
            var response = await RecordAsync(ReceiptBody(accountId: long.MaxValue));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var errors = (JArray)(await ReadJsonAsync(response)).GetValue("errors", StringComparison.OrdinalIgnoreCase)!;
            Assert.Contains("existing bank account is required", errors[0]!.ToString());
            await AssertNothingSavedAsync();
        }

        [Fact]
        public async Task RecordMiscReceipt_InactiveAccount_Returns400AndSavesNothing()
        {
            var response = await RecordAsync(ReceiptBody(accountId: _inactiveAccountId));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var errors = (JArray)(await ReadJsonAsync(response)).GetValue("errors", StringComparison.OrdinalIgnoreCase)!;
            Assert.Contains("must be active", errors[0]!.ToString());
            await AssertNothingSavedAsync();
        }

        [Fact]
        public async Task RecordMiscReceipt_ReferenceTooLong_Returns400AndSavesNothing()
        {
            var response = await RecordAsync(ReceiptBody(reference: new string('R', 101)));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var errors = (JArray)(await ReadJsonAsync(response)).GetValue("errors", StringComparison.OrdinalIgnoreCase)!;
            Assert.Contains("Reference cannot exceed 100 characters", errors[0]!.ToString());
            await AssertNothingSavedAsync();
        }

        [Fact]
        public async Task RecordMiscReceipt_EmptyBody_Returns400AndSavesNothing()
        {
            var response = await _testClient.PostAsync("Receipt/RecordMiscReceipt",
                new StringContent("", Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            await AssertNothingSavedAsync();
        }

        // ---- Only POST exists ----

        [Theory]
        [InlineData("GET")]
        [InlineData("PUT")]
        [InlineData("DELETE")]
        public async Task RecordMiscReceipt_OtherHttpMethods_Return405(string method)
        {
            var request = new HttpRequestMessage(new HttpMethod(method), "Receipt/RecordMiscReceipt");

            var response = await _testClient.SendAsync(request);

            Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        }
    }
}
