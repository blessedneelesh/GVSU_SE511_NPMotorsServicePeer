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
    // POST /SalesInvoice/PostInvoice?id= (Story 14) against the test database.
    // Draft total: vehicle selling price 18000 + tax 1000 + fees 200 - discount 700 = 18500.
    public class SalesInvoicePostInvoiceControllerTest : TestBase, IAsyncLifetime
    {
        private const decimal Total = 18500m;
        private const decimal OpeningBalance = 500m;

        private readonly string _marker = $"itest-{Guid.NewGuid():N}".Substring(0, 12);
        private long _bankId;
        private long _accountId;
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

        private async Task<(long InvoiceId, long VehicleId)> CreateDraftAsync()
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
                Customer_Id = _customerId,
                Salesperson_Id = _salespersonId,
                Vehicle_Id = vehicle.Id,
                Invoice_Date = new DateTime(2026, 5, 1),
                Due_Date = new DateTime(2026, 5, 31),
                Tax = 1000m, Fees = 200m, Discount = 700m
            });
            return (draft.Id, vehicle.Id);
        }

        private static StringContent Json(object? body) =>
            new(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");

        private Task<HttpResponseMessage> PostAsync(long id, object body) =>
            _testClient.PostAsync($"SalesInvoice/PostInvoice?id={id}", Json(body));

        private object PaidInFullBody(long? accountId = null, string method = "card") => new
        {
            paymentCondition = "paidInFull", amountReceived = Total, paymentMethod = method,
            reference = "REF-1", bankAccountId = accountId ?? _accountId, receiptDate = "2026-11-08"
        };

        private object PartialBody(decimal amount = 6000m) => new
        {
            paymentCondition = "partialPayment", amountReceived = amount, paymentMethod = "transfer",
            reference = "WIRE-77", bankAccountId = _accountId, receiptDate = "2026-11-08"
        };

        private static object CreditBody() => new { paymentCondition = "credit", amountReceived = 0 };

        private static async Task<SalesInvoiceResponseDto> ReadAsync(HttpResponseMessage response) =>
            JsonConvert.DeserializeObject<SalesInvoiceResponseDto>(await response.Content.ReadAsStringAsync())!;

        private static async Task<JObject> ReadJsonAsync(HttpResponseMessage response) =>
            JObject.Parse(await response.Content.ReadAsStringAsync());

        private async Task<T> QueryAsync<T>(string sql, object param)
        {
            using var connection = _repositoryContext.GetDatabaseConnection();
            return await connection.ExecuteScalarAsync<T>(sql, param) ?? default!;
        }

        private Task<string> LifecycleAsync(long invoiceId) =>
            QueryAsync<string>("SELECT lifecycle FROM sales_invoice WHERE id = @Id", new { Id = invoiceId });

        private Task<decimal> BookBalanceAsync(long accountId) =>
            QueryAsync<decimal>("SELECT book_balance FROM bank_account WHERE id = @Id", new { Id = accountId });

        private async Task AssertNothingSavedAsync(long invoiceId)
        {
            Assert.Equal("draft", await LifecycleAsync(invoiceId));
            Assert.Equal(0, await QueryAsync<int>("SELECT COUNT(*) FROM ar_invoice WHERE invoice_id = @Id", new { Id = invoiceId }));
            Assert.Equal(0, await QueryAsync<int>("SELECT COUNT(*) FROM bank_receipt WHERE sales_invoice_id = @Id", new { Id = invoiceId }));
            Assert.Equal(0, await QueryAsync<int>(
                @"SELECT COUNT(*) FROM ""transaction"" WHERE bank_account_id IN (SELECT id FROM bank_account WHERE bank_id = @BankId)",
                new { BankId = _bankId }));
            Assert.Equal(OpeningBalance, await BookBalanceAsync(_accountId));
        }

        // ---- 200: the three payment conditions (AC 1-3) ----

        [Fact]
        public async Task PostInvoice_PaidInFull_Returns200WithPaidInvoiceAndReceiptButNoAr()
        {
            var (invoiceId, _) = await CreateDraftAsync();

            var response = await PostAsync(invoiceId, PaidInFullBody());

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await ReadAsync(response);
            Assert.Equal(invoiceId, body.Id);
            Assert.Equal(Total, body.Total);
            Assert.Equal(Total, body.Paid);
            Assert.Equal(0m, body.Balance);
            Assert.Equal(PaymentStatus.Paid, body.Payment_Status);
            Assert.Equal(InvoiceLifecycle.Posted, body.Lifecycle);
            Assert.Null(body.Ar_Invoice_Id);
            Assert.NotNull(body.Bank_Receipt_Id);
        }

        [Fact]
        public async Task PostInvoice_Partial_Returns200WithPartialInvoiceArAndReceipt()
        {
            var (invoiceId, _) = await CreateDraftAsync();

            var response = await PostAsync(invoiceId, PartialBody(6000m));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await ReadAsync(response);
            Assert.Equal(6000m, body.Paid);
            Assert.Equal(12500m, body.Balance);
            Assert.Equal(PaymentStatus.Partial, body.Payment_Status);
            Assert.NotNull(body.Ar_Invoice_Id);
            Assert.NotNull(body.Bank_Receipt_Id);
        }

        [Fact]
        public async Task PostInvoice_Credit_Returns200WithUnpaidInvoiceArAndNoReceipt()
        {
            var (invoiceId, _) = await CreateDraftAsync();

            var response = await PostAsync(invoiceId, CreditBody());

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await ReadAsync(response);
            Assert.Equal(0m, body.Paid);
            Assert.Equal(Total, body.Balance);
            Assert.Equal(PaymentStatus.Unpaid, body.Payment_Status);
            Assert.NotNull(body.Ar_Invoice_Id);
            Assert.Null(body.Bank_Receipt_Id);
        }

        [Fact]
        public async Task PostInvoice_Returns200WithTheDocumentedFields()
        {
            var (invoiceId, _) = await CreateDraftAsync();

            var response = await PostAsync(invoiceId, PartialBody(6000m));

            var json = await ReadJsonAsync(response);
            foreach (var name in new[] { "id", "number", "total", "paid", "balance", "payment_status", "lifecycle", "ar_invoice_id", "bank_receipt_id" })
                Assert.NotNull(json.GetValue(name, StringComparison.OrdinalIgnoreCase));
            Assert.Equal("partial", json.GetValue("payment_status", StringComparison.OrdinalIgnoreCase)!.ToString());
            Assert.Equal("posted", json.GetValue("lifecycle", StringComparison.OrdinalIgnoreCase)!.ToString());
        }

        [Fact]
        public async Task PostInvoice_Paid_SavesInvoiceVehicleAndRaisesTheBookBalance()
        {
            var (invoiceId, vehicleId) = await CreateDraftAsync();

            await PostAsync(invoiceId, PaidInFullBody());

            Assert.Equal("posted", await LifecycleAsync(invoiceId));
            Assert.Equal("sold", await QueryAsync<string>("SELECT LOWER(status) FROM vehicle WHERE id = @Id", new { Id = vehicleId }));
            Assert.Equal(OpeningBalance + Total, await BookBalanceAsync(_accountId));
            Assert.Equal(1, await QueryAsync<int>("SELECT COUNT(*) FROM bank_receipt WHERE sales_invoice_id = @Id", new { Id = invoiceId }));
        }

        [Theory]
        [InlineData("cash")]
        [InlineData("card")]
        [InlineData("transfer")]
        [InlineData("check")]
        public async Task PostInvoice_EachAllowedMethod_Returns200(string method)
        {
            var (invoiceId, _) = await CreateDraftAsync();

            var response = await PostAsync(invoiceId, PaidInFullBody(method: method));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // ---- 404 ----

        [Fact]
        public async Task PostInvoice_UnknownInvoice_Returns404()
        {
            var response = await PostAsync(long.MaxValue, CreditBody());

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        // ---- 400: validation rules, nothing saved ----

        [Fact]
        public async Task PostInvoice_NegativeAmount_Returns400WithErrorsAndSavesNothing()
        {
            var (invoiceId, _) = await CreateDraftAsync();

            var response = await PostAsync(invoiceId, PartialBody(-1m));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var errors = (await ReadJsonAsync(response))["errors"]!.ToObject<string[]>()!;
            Assert.Contains(errors, e => e.Contains("cannot be negative"));
            await AssertNothingSavedAsync(invoiceId);
        }

        [Fact]
        public async Task PostInvoice_AmountAboveTotal_Returns400AndSavesNothing()
        {
            var (invoiceId, _) = await CreateDraftAsync();

            var response = await PostAsync(invoiceId, new
            {
                paymentCondition = "paidInFull", amountReceived = Total + 0.01m, paymentMethod = "card", bankAccountId = _accountId
            });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var errors = (await ReadJsonAsync(response))["errors"]!.ToObject<string[]>()!;
            Assert.Contains(errors, e => e.Contains("cannot exceed the invoice total"));
            await AssertNothingSavedAsync(invoiceId);
        }

        [Fact]
        public async Task PostInvoice_PaidInFullWithPartialAmount_Returns400()
        {
            var (invoiceId, _) = await CreateDraftAsync();

            var response = await PostAsync(invoiceId, new
            {
                paymentCondition = "paidInFull", amountReceived = 6000, paymentMethod = "card", bankAccountId = _accountId
            });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            await AssertNothingSavedAsync(invoiceId);
        }

        [Fact]
        public async Task PostInvoice_PartialEqualToTotal_Returns400()
        {
            var (invoiceId, _) = await CreateDraftAsync();

            var response = await PostAsync(invoiceId, PartialBody(Total));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            await AssertNothingSavedAsync(invoiceId);
        }

        [Fact]
        public async Task PostInvoice_CreditWithAmount_Returns400()
        {
            var (invoiceId, _) = await CreateDraftAsync();

            var response = await PostAsync(invoiceId, new { paymentCondition = "credit", amountReceived = 100 });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            await AssertNothingSavedAsync(invoiceId);
        }

        [Fact]
        public async Task PostInvoice_UnknownPaymentMethod_Returns400AndSavesNothing()
        {
            var (invoiceId, _) = await CreateDraftAsync();

            var response = await PostAsync(invoiceId, PaidInFullBody(method: "bitcoin"));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            await AssertNothingSavedAsync(invoiceId);
        }

        [Fact]
        public async Task PostInvoice_MissingPaymentMethodWithAmount_Returns400()
        {
            var (invoiceId, _) = await CreateDraftAsync();

            var response = await PostAsync(invoiceId, new
            {
                paymentCondition = "paidInFull", amountReceived = Total, bankAccountId = _accountId
            });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            await AssertNothingSavedAsync(invoiceId);
        }

        [Fact]
        public async Task PostInvoice_UnknownPaymentCondition_Returns400()
        {
            var (invoiceId, _) = await CreateDraftAsync();

            var response = await PostAsync(invoiceId, new { paymentCondition = "layaway", amountReceived = 0 });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            await AssertNothingSavedAsync(invoiceId);
        }

        [Fact]
        public async Task PostInvoice_AmountWithoutBankAccount_Returns400AndSavesNothing()
        {
            var (invoiceId, _) = await CreateDraftAsync();

            var response = await PostAsync(invoiceId, new
            {
                paymentCondition = "paidInFull", amountReceived = Total, paymentMethod = "card"
            });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var errors = (await ReadJsonAsync(response))["errors"]!.ToObject<string[]>()!;
            Assert.Contains(errors, e => e.Contains("bank account is required"));
            await AssertNothingSavedAsync(invoiceId);
        }

        [Fact]
        public async Task PostInvoice_InactiveBankAccount_Returns400AndSavesNothing()
        {
            var (invoiceId, _) = await CreateDraftAsync();

            var response = await PostAsync(invoiceId, PaidInFullBody(accountId: _inactiveAccountId));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var errors = (await ReadJsonAsync(response))["errors"]!.ToObject<string[]>()!;
            Assert.Contains(errors, e => e.Contains("must be active"));
            await AssertNothingSavedAsync(invoiceId);
        }

        [Fact]
        public async Task PostInvoice_CreditNeedsNoBankAccount_Returns200()
        {
            var (invoiceId, _) = await CreateDraftAsync();

            var response = await PostAsync(invoiceId, CreditBody());

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        [Fact]
        public async Task PostInvoice_AlreadyPosted_Returns400AndDoesNotPostTwice()
        {
            var (invoiceId, _) = await CreateDraftAsync();
            await PostAsync(invoiceId, PaidInFullBody());

            var response = await PostAsync(invoiceId, PaidInFullBody());

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var errors = (await ReadJsonAsync(response))["errors"]!.ToObject<string[]>()!;
            Assert.Contains(errors, e => e.Contains("Draft"));
            Assert.Equal(1, await QueryAsync<int>("SELECT COUNT(*) FROM bank_receipt WHERE sales_invoice_id = @Id", new { Id = invoiceId }));
            Assert.Equal(OpeningBalance + Total, await BookBalanceAsync(_accountId));
        }

        // ---- 409 ----

        [Fact]
        public async Task PostInvoice_VehicleAlreadySold_Returns409AndSavesNothing()
        {
            var (invoiceId, vehicleId) = await CreateDraftAsync();
            using (var connection = _repositoryContext.GetDatabaseConnection())
                await connection.ExecuteAsync("UPDATE vehicle SET status = 'sold' WHERE id = @Id", new { Id = vehicleId });

            var response = await PostAsync(invoiceId, PartialBody());

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Contains("already sold", (await ReadJsonAsync(response))["message"]!.ToString());
            await AssertNothingSavedAsync(invoiceId);
        }

        // ---- binding and routing ----

        [Fact]
        public async Task PostInvoice_NonNumericId_Returns400()
        {
            var response = await _testClient.PostAsync("SalesInvoice/PostInvoice?id=abc", Json(CreditBody()));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task PostInvoice_NonJsonBody_Returns400()
        {
            var (invoiceId, _) = await CreateDraftAsync();

            var response = await _testClient.PostAsync($"SalesInvoice/PostInvoice?id={invoiceId}",
                new StringContent("not json", Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            await AssertNothingSavedAsync(invoiceId);
        }

        [Fact]
        public async Task PostInvoice_GetIsNotAllowed_Returns405()
        {
            var (invoiceId, _) = await CreateDraftAsync();

            var response = await _testClient.GetAsync($"SalesInvoice/PostInvoice?id={invoiceId}");

            Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        }
    }
}
