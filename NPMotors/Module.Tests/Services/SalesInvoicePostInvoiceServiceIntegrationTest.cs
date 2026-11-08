using System.ComponentModel.DataAnnotations;
using Dapper;
using NPMotors.Repository.Exceptions;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.Models.Enums;
using NPMotors.Repository.Repositories;
using NPMotors.Services.Service;

namespace Module.Tests.Services
{
    // PostInvoice through the real repositories and the test database (AC 1-8 end to end).
    // Draft total: vehicle selling price 18000 + tax 1000 + fees 200 - discount 700 = 18500.
    public class SalesInvoicePostInvoiceServiceIntegrationTest : TestBase, IAsyncLifetime
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
        private SalesInvoiceRepository _invoiceRepository = null!;
        private SalesInvoiceService _service = null!;

        private class InvoiceRow
        {
            public string Lifecycle { get; set; } = "";
            public string? Payment_Status { get; set; }
            public decimal Total { get; set; }
            public decimal Paid { get; set; }
            public decimal Balance { get; set; }
        }

        private class ArRow
        {
            public long Id { get; set; }
            public long Invoice_Id { get; set; }
            public string Number { get; set; } = "";
            public decimal Amount { get; set; }
            public decimal Paid { get; set; }
            public decimal Balance { get; set; }
            public string Status { get; set; } = "";
        }

        private class ReceiptRow
        {
            public long Id { get; set; }
            public long Transaction_Id { get; set; }
            public long Bank_Account_Id { get; set; }
            public long? Customer_Id { get; set; }
            public long? Sales_Invoice_Id { get; set; }
            public long? Ar_Invoice_Id { get; set; }
            public string Receipt_Number { get; set; } = "";
            public string Payment_Method { get; set; } = "";
            public string? Reference { get; set; }
            public decimal Amount { get; set; }
            public string Status { get; set; } = "";
        }

        private class TransactionRow
        {
            public long Id { get; set; }
            public string Type { get; set; } = "";
            public string Direction { get; set; } = "";
            public decimal Amount { get; set; }
            public string Status { get; set; } = "";
            public string Source_Number { get; set; } = "";
        }

        private class StatementLineRow
        {
            public long Transaction_Id { get; set; }
            public decimal Deposit { get; set; }
            public decimal Withdrawal { get; set; }
            public decimal Running_Balance { get; set; }
            public string Statement_Status { get; set; } = "";
            public string Match_Status { get; set; } = "";
        }

        public async Task InitializeAsync()
        {
            var accounts = new BankAccountRepository(_repositoryContext);
            var posting = new TransactionPostingService(
                new TransactionRepository(_repositoryContext), accounts,
                new BankStatementLineRepository(_repositoryContext), _repositoryContext);
            _invoiceRepository = new SalesInvoiceRepository(_repositoryContext);
            _service = new SalesInvoiceService(
                _invoiceRepository, new VehicleRepository(_repositoryContext), accounts,
                new ArInvoiceRepository(_repositoryContext), new BankReceiptRepository(_repositoryContext), posting, _repositoryContext,
                new CustomerBalanceService(new CustomerRepository(_repositoryContext)));

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
            // Foreign-key order: receipts, statement lines, transactions, AR, invoices, vehicles, vendors, people, banking.
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
            var draft = await _invoiceRepository.AddSalesInvoice(new SalesInvoiceCreateDto
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

        private static PostInvoiceRequest PaidInFull(long accountId, PaymentMethod method = PaymentMethod.Card, string? reference = "REF-1") => new()
        {
            PaymentCondition = PaymentCondition.PaidInFull, AmountReceived = Total, PaymentMethod = method,
            Reference = reference, BankAccountId = accountId, ReceiptDate = new DateTime(2026, 11, 8)
        };

        private static PostInvoiceRequest Partial(long accountId, decimal amount = 6000m) => new()
        {
            PaymentCondition = PaymentCondition.PartialPayment, AmountReceived = amount, PaymentMethod = PaymentMethod.Transfer,
            Reference = "WIRE-77", BankAccountId = accountId, ReceiptDate = new DateTime(2026, 11, 8)
        };

        private static PostInvoiceRequest Credit() => new() { PaymentCondition = PaymentCondition.Credit, AmountReceived = 0m };

        private async Task<InvoiceRow> InvoiceAsync(long id)
        {
            using var connection = _repositoryContext.GetDatabaseConnection();
            return await connection.QuerySingleAsync<InvoiceRow>(
                "SELECT lifecycle, payment_status, total, paid, balance FROM sales_invoice WHERE id = @Id", new { Id = id });
        }

        private async Task<List<ArRow>> ArRowsAsync(long invoiceId)
        {
            using var connection = _repositoryContext.GetDatabaseConnection();
            return (await connection.QueryAsync<ArRow>(
                "SELECT id, invoice_id, number, amount, paid, balance, status FROM ar_invoice WHERE invoice_id = @Id", new { Id = invoiceId })).ToList();
        }

        private async Task<List<ReceiptRow>> ReceiptsAsync(long invoiceId)
        {
            using var connection = _repositoryContext.GetDatabaseConnection();
            return (await connection.QueryAsync<ReceiptRow>(
                @"SELECT id, transaction_id, bank_account_id, customer_id, sales_invoice_id, ar_invoice_id, receipt_number,
                         payment_method, reference, amount, status
                  FROM bank_receipt WHERE sales_invoice_id = @Id", new { Id = invoiceId })).ToList();
        }

        private async Task<List<TransactionRow>> TransactionsAsync()
        {
            using var connection = _repositoryContext.GetDatabaseConnection();
            return (await connection.QueryAsync<TransactionRow>(
                @"SELECT id, type, direction::text AS direction, amount, status, source_number
                  FROM ""transaction"" WHERE bank_account_id IN (SELECT id FROM bank_account WHERE bank_id = @BankId) ORDER BY id",
                new { BankId = _bankId })).ToList();
        }

        private async Task<List<StatementLineRow>> StatementLinesAsync()
        {
            using var connection = _repositoryContext.GetDatabaseConnection();
            return (await connection.QueryAsync<StatementLineRow>(
                @"SELECT transaction_id, deposit, withdrawal, running_balance, statement_status, match_status
                  FROM bank_statement_line WHERE bank_account_id IN (SELECT id FROM bank_account WHERE bank_id = @BankId) ORDER BY id",
                new { BankId = _bankId })).ToList();
        }

        private async Task<decimal> BookBalanceAsync(long accountId)
        {
            using var connection = _repositoryContext.GetDatabaseConnection();
            return await connection.ExecuteScalarAsync<decimal>("SELECT book_balance FROM bank_account WHERE id = @Id", new { Id = accountId });
        }

        private async Task<string> VehicleStatusAsync(long vehicleId)
        {
            using var connection = _repositoryContext.GetDatabaseConnection();
            return await connection.ExecuteScalarAsync<string>("SELECT LOWER(status) FROM vehicle WHERE id = @Id", new { Id = vehicleId });
        }

        private async Task AssertNothingSavedAsync(long invoiceId, long vehicleId)
        {
            var invoice = await InvoiceAsync(invoiceId);
            Assert.Equal("draft", invoice.Lifecycle);
            Assert.Equal(0m, invoice.Paid);
            Assert.Equal("available", await VehicleStatusAsync(vehicleId));
            Assert.Empty(await ArRowsAsync(invoiceId));
            Assert.Empty(await ReceiptsAsync(invoiceId));
            Assert.Empty(await TransactionsAsync());
            Assert.Empty(await StatementLinesAsync());
            Assert.Equal(OpeningBalance, await BookBalanceAsync(_accountId));
        }

        // ---- AC 1: paid in full ----

        [Fact]
        public async Task PostInvoice_PaidInFull_InvoiceIsPaidWithZeroBalance()
        {
            var (invoiceId, vehicleId) = await CreateDraftAsync();

            var result = await _service.PostInvoice(invoiceId, PaidInFull(_accountId));

            Assert.Equal(Total, result!.Paid);
            Assert.Equal(0m, result.Balance);
            Assert.Equal(PaymentStatus.Paid, result.Payment_Status);
            var row = await InvoiceAsync(invoiceId);
            Assert.Equal("posted", row.Lifecycle);
            Assert.Equal("paid", row.Payment_Status);
            Assert.Equal(Total, row.Paid);
            Assert.Equal(0m, row.Balance);
            Assert.Equal("sold", await VehicleStatusAsync(vehicleId));
        }

        [Fact]
        public async Task PostInvoice_PaidInFull_CreatesExactlyOneReceiptAndNoArRecord()
        {
            var (invoiceId, _) = await CreateDraftAsync();

            var result = await _service.PostInvoice(invoiceId, PaidInFull(_accountId, PaymentMethod.Check, "CHK-1001"));

            Assert.Empty(await ArRowsAsync(invoiceId));
            Assert.Null(result!.Ar_Invoice_Id);
            var receipt = Assert.Single(await ReceiptsAsync(invoiceId));
            Assert.Equal(result.Bank_Receipt_Id, receipt.Id);
            Assert.Equal(_accountId, receipt.Bank_Account_Id);
            Assert.Equal(_customerId, receipt.Customer_Id);
            Assert.Equal(invoiceId, receipt.Sales_Invoice_Id);
            Assert.Null(receipt.Ar_Invoice_Id);
            Assert.StartsWith("RCT-", receipt.Receipt_Number);
            Assert.EndsWith(receipt.Id.ToString("D6"), receipt.Receipt_Number);
            Assert.Equal("Check", receipt.Payment_Method);
            Assert.Equal("CHK-1001", receipt.Reference);
            Assert.Equal(Total, receipt.Amount);
            Assert.Equal("Posted", receipt.Status);
        }

        // ---- AC 8 / money shows straight away: transaction, statement line, book balance ----

        [Fact]
        public async Task PostInvoice_PaidInFull_PostsTransactionStatementLineAndRaisesBookBalance()
        {
            var (invoiceId, _) = await CreateDraftAsync();

            var result = await _service.PostInvoice(invoiceId, PaidInFull(_accountId));

            var transaction = Assert.Single(await TransactionsAsync());
            Assert.Equal("BankReceipt", transaction.Type);
            Assert.Equal("in", transaction.Direction);
            Assert.Equal(Total, transaction.Amount);
            Assert.Equal("posted", transaction.Status);
            Assert.Equal(result!.Number, transaction.Source_Number);

            var receipt = Assert.Single(await ReceiptsAsync(invoiceId));
            Assert.Equal(transaction.Id, receipt.Transaction_Id);

            var line = Assert.Single(await StatementLinesAsync());
            Assert.Equal(transaction.Id, line.Transaction_Id);
            Assert.Equal(18500m, line.Deposit);
            Assert.Equal(0m, line.Withdrawal);
            Assert.Equal(19000m, line.Running_Balance);
            Assert.Equal("Cleared", line.Statement_Status);
            Assert.Equal("Matched", line.Match_Status);

            Assert.Equal(19000m, await BookBalanceAsync(_accountId));
        }

        // ---- AC 2, 4: partial payment ----

        [Fact]
        public async Task PostInvoice_Partial_InvoiceIsPartialWithRemainingBalance()
        {
            var (invoiceId, _) = await CreateDraftAsync();

            var result = await _service.PostInvoice(invoiceId, Partial(_accountId, 6000m));

            Assert.Equal(6000m, result!.Paid);
            Assert.Equal(12500m, result.Balance);
            Assert.Equal(PaymentStatus.Partial, result.Payment_Status);
            var row = await InvoiceAsync(invoiceId);
            Assert.Equal("partial", row.Payment_Status);
            Assert.Equal(6000m, row.Paid);
            Assert.Equal(12500m, row.Balance);
        }

        [Fact]
        public async Task PostInvoice_Partial_CreatesOneArInvoiceMatchingTheSalesInvoice()
        {
            var (invoiceId, _) = await CreateDraftAsync();

            var result = await _service.PostInvoice(invoiceId, Partial(_accountId, 6000m));

            var ar = Assert.Single(await ArRowsAsync(invoiceId));
            Assert.Equal(result!.Ar_Invoice_Id, ar.Id);
            Assert.StartsWith("AR-", ar.Number);
            Assert.EndsWith(ar.Id.ToString("D6"), ar.Number);
            var invoice = await InvoiceAsync(invoiceId);
            Assert.Equal(invoice.Total, ar.Amount);
            Assert.Equal(invoice.Paid, ar.Paid);
            Assert.Equal(invoice.Balance, ar.Balance);
            Assert.Equal(18500m, ar.Amount);
            Assert.Equal(6000m, ar.Paid);
            Assert.Equal(12500m, ar.Balance);
            Assert.Equal("partial", ar.Status);
        }

        [Fact]
        public async Task PostInvoice_Partial_ReceiptLinksToSalesInvoiceAndArInvoiceAndMoneyReachesTheAccount()
        {
            var (invoiceId, _) = await CreateDraftAsync();

            await _service.PostInvoice(invoiceId, Partial(_accountId, 6000m));

            var ar = Assert.Single(await ArRowsAsync(invoiceId));
            var receipt = Assert.Single(await ReceiptsAsync(invoiceId));
            Assert.Equal(invoiceId, receipt.Sales_Invoice_Id);
            Assert.Equal(ar.Id, receipt.Ar_Invoice_Id);
            Assert.Equal(6000m, receipt.Amount);
            Assert.Equal("Transfer", receipt.Payment_Method);
            Assert.Equal(6500m, await BookBalanceAsync(_accountId));
            Assert.Equal(6500m, Assert.Single(await StatementLinesAsync()).Running_Balance);
        }

        // ---- AC 3, 4: credit sale ----

        [Fact]
        public async Task PostInvoice_Credit_InvoiceIsUnpaidWithFullBalance()
        {
            var (invoiceId, vehicleId) = await CreateDraftAsync();

            var result = await _service.PostInvoice(invoiceId, Credit());

            Assert.Equal(0m, result!.Paid);
            Assert.Equal(Total, result.Balance);
            Assert.Equal(PaymentStatus.Unpaid, result.Payment_Status);
            var row = await InvoiceAsync(invoiceId);
            Assert.Equal("posted", row.Lifecycle);
            Assert.Equal("unpaid", row.Payment_Status);
            Assert.Equal("sold", await VehicleStatusAsync(vehicleId));
        }

        [Fact]
        public async Task PostInvoice_Credit_CreatesArForTheFullTotalAndNoReceiptTransactionOrBalanceChange()
        {
            var (invoiceId, _) = await CreateDraftAsync();

            var result = await _service.PostInvoice(invoiceId, Credit());

            var ar = Assert.Single(await ArRowsAsync(invoiceId));
            Assert.Equal(result!.Ar_Invoice_Id, ar.Id);
            Assert.Equal(18500m, ar.Amount);
            Assert.Equal(0m, ar.Paid);
            Assert.Equal(18500m, ar.Balance);
            Assert.Equal("unpaid", ar.Status);
            Assert.Empty(await ReceiptsAsync(invoiceId));
            Assert.Null(result.Bank_Receipt_Id);
            Assert.Empty(await TransactionsAsync());
            Assert.Empty(await StatementLinesAsync());
            Assert.Equal(OpeningBalance, await BookBalanceAsync(_accountId));
        }

        // ---- AC 7: bank account rules, nothing saved ----

        [Fact]
        public async Task PostInvoice_InactiveBankAccount_IsRejectedAndNothingIsSaved()
        {
            var (invoiceId, vehicleId) = await CreateDraftAsync();

            await Assert.ThrowsAsync<ValidationException>(() => _service.PostInvoice(invoiceId, PaidInFull(_inactiveAccountId)));

            await AssertNothingSavedAsync(invoiceId, vehicleId);
            Assert.Equal(OpeningBalance, await BookBalanceAsync(_inactiveAccountId));
        }

        [Fact]
        public async Task PostInvoice_UnknownBankAccount_IsRejectedAndNothingIsSaved()
        {
            var (invoiceId, vehicleId) = await CreateDraftAsync();

            await Assert.ThrowsAsync<ValidationException>(() => _service.PostInvoice(invoiceId, PaidInFull(long.MaxValue)));

            await AssertNothingSavedAsync(invoiceId, vehicleId);
        }

        [Fact]
        public async Task PostInvoice_NoBankAccountForAnAmountReceived_IsRejectedAndNothingIsSaved()
        {
            var (invoiceId, vehicleId) = await CreateDraftAsync();
            var request = PaidInFull(_accountId);
            request.BankAccountId = null;

            await Assert.ThrowsAsync<ValidationException>(() => _service.PostInvoice(invoiceId, request));

            await AssertNothingSavedAsync(invoiceId, vehicleId);
        }

        // ---- AC 5: amount limits, nothing saved ----

        [Fact]
        public async Task PostInvoice_AmountAboveTotal_IsRejectedAndNothingIsSaved()
        {
            var (invoiceId, vehicleId) = await CreateDraftAsync();
            var request = PaidInFull(_accountId);
            request.AmountReceived = Total + 0.01m;

            await Assert.ThrowsAsync<ValidationException>(() => _service.PostInvoice(invoiceId, request));

            await AssertNothingSavedAsync(invoiceId, vehicleId);
        }

        [Fact]
        public async Task PostInvoice_NegativeAmount_IsRejectedAndNothingIsSaved()
        {
            var (invoiceId, vehicleId) = await CreateDraftAsync();

            await Assert.ThrowsAsync<ValidationException>(() => _service.PostInvoice(invoiceId, Partial(_accountId, -1m)));

            await AssertNothingSavedAsync(invoiceId, vehicleId);
        }

        // ---- AC 8: everything saves together or not at all ----

        [Fact]
        public async Task PostInvoice_VehicleAlreadySold_ThrowsConflictAndRollsBackEverything()
        {
            var (invoiceId, vehicleId) = await CreateDraftAsync();
            using (var connection = _repositoryContext.GetDatabaseConnection())
                await connection.ExecuteAsync("UPDATE vehicle SET status = 'sold' WHERE id = @Id", new { Id = vehicleId });

            await Assert.ThrowsAsync<ConflictException>(() => _service.PostInvoice(invoiceId, Partial(_accountId)));

            var invoice = await InvoiceAsync(invoiceId);
            Assert.Equal("draft", invoice.Lifecycle);
            Assert.Equal(0m, invoice.Paid);
            Assert.Empty(await ArRowsAsync(invoiceId));
            Assert.Empty(await ReceiptsAsync(invoiceId));
            Assert.Empty(await TransactionsAsync());
            Assert.Empty(await StatementLinesAsync());
            Assert.Equal(OpeningBalance, await BookBalanceAsync(_accountId));
        }

        [Fact]
        public async Task PostInvoice_SecondPostedInvoiceForTheSameVehicle_ThrowsConflictAndRollsBackEverything()
        {
            var (firstId, vehicleId) = await CreateDraftAsync();
            var secondDraft = await _invoiceRepository.AddSalesInvoice(new SalesInvoiceCreateDto
            {
                Customer_Id = _customerId, Salesperson_Id = _salespersonId, Vehicle_Id = vehicleId,
                Invoice_Date = new DateTime(2026, 5, 2), Tax = 1000m, Fees = 200m, Discount = 700m
            });
            await _service.PostInvoice(firstId, Credit());

            await Assert.ThrowsAnyAsync<ConflictException>(() => _service.PostInvoice(secondDraft.Id, PaidInFull(_accountId)));

            Assert.Equal("draft", (await InvoiceAsync(secondDraft.Id)).Lifecycle);
            Assert.Empty(await ReceiptsAsync(secondDraft.Id));
            Assert.Empty(await ArRowsAsync(secondDraft.Id));
            Assert.Equal(OpeningBalance, await BookBalanceAsync(_accountId));
        }

        // ---- Only a Draft can be posted ----

        [Fact]
        public async Task PostInvoice_AlreadyPosted_ThrowsValidationAndCreatesNoSecondReceiptOrBalanceChange()
        {
            var (invoiceId, _) = await CreateDraftAsync();
            await _service.PostInvoice(invoiceId, PaidInFull(_accountId));

            var ex = await Assert.ThrowsAsync<ValidationException>(() => _service.PostInvoice(invoiceId, PaidInFull(_accountId)));

            Assert.Contains("Draft", ex.Message);
            Assert.Single(await ReceiptsAsync(invoiceId));
            Assert.Single(await TransactionsAsync());
            Assert.Equal(19000m, await BookBalanceAsync(_accountId));
        }

        [Fact]
        public async Task PostInvoice_UnknownInvoice_ReturnsNull()
        {
            var result = await _service.PostInvoice(long.MaxValue, Credit());

            Assert.Null(result);
        }
    }
}
