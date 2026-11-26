using System.Data;
using Dapper;
using NPMotors.Repository.Models;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.Models.Enums;
using NPMotors.Repository.Repositories;

namespace NPMotors.Repository.Tests.DataAccess
{
    // Story 20: BankPaymentRepository (InsertBankPayment, GetPaymentsByBillId). The bank, account, vendor and AP bills
    // are committed in InitializeAsync and removed in DisposeAsync; inserts run in a transaction that is never committed.
    public class BankPaymentRepositoryTest : TestBase, IAsyncLifetime
    {
        private readonly BankPaymentRepository _repository;
        private readonly string _marker = $"itest-{Guid.NewGuid():N}".Substring(0, 12);
        private long _bankId;
        private long _accountId;
        private long _vendorId;
        private long _billId;
        private long _otherBillId;

        public BankPaymentRepositoryTest()
        {
            _repository = new BankPaymentRepository(_repositoryContext);
        }

        private class PaymentRow
        {
            public long Transaction_Id { get; set; }
            public long Bank_Account_Id { get; set; }
            public long? Vendor_Id { get; set; }
            public long? Ap_Bill_Id { get; set; }
            public long? Vehicle_Id { get; set; }
            public string Payment_Number { get; set; } = "";
            public DateTime Payment_Date { get; set; }
            public string Payment_Method { get; set; } = "";
            public string? Reference { get; set; }
            public decimal Amount { get; set; }
            public string Status { get; set; } = "";
            public string Created_By { get; set; } = "";
        }

        private const string PaymentSelect =
            @"SELECT transaction_id, bank_account_id, vendor_id, ap_bill_id, vehicle_id, payment_number, payment_date,
                     payment_method, reference, amount, status, created_by
              FROM bank_payment WHERE id = @Id";

        public async Task InitializeAsync()
        {
            using IDbConnection connection = _repositoryContext.GetDatabaseConnection();
            _bankId = await connection.ExecuteScalarAsync<long>(
                "INSERT INTO bank (name) VALUES (@Name) RETURNING id", new { Name = $"{_marker} Bank" });
            _accountId = await connection.ExecuteScalarAsync<long>(
                @"INSERT INTO bank_account (bank_id, name, type, opening_balance, book_balance, status)
                  VALUES (@BankId, @Name, 'Checking', 5000, 5000, 'active') RETURNING id",
                new { BankId = _bankId, Name = $"{_marker} Account" });
            _vendorId = (await new VendorRepository(_repositoryContext).AddVendor(new VendorCreateDto
            {
                Name = $"{_marker} Vendor", Type = VendorType.Vehicles, Status = Status.Active
            })).Id;
            _billId = await InsertBillAsync(connection, "A");
            _otherBillId = await InsertBillAsync(connection, "B");
        }

        public async Task DisposeAsync()
        {
            using IDbConnection connection = _repositoryContext.GetDatabaseConnection();
            var pattern = new { Pattern = _marker + "%", BankId = _bankId };
            await connection.ExecuteAsync("DELETE FROM bank_payment WHERE bank_account_id = @BankId OR bank_account_id IN (SELECT id FROM bank_account WHERE bank_id = @BankId)", pattern);
            await connection.ExecuteAsync("DELETE FROM bank_statement_line WHERE bank_account_id IN (SELECT id FROM bank_account WHERE bank_id = @BankId)", pattern);
            await connection.ExecuteAsync(@"DELETE FROM ""transaction"" WHERE bank_account_id IN (SELECT id FROM bank_account WHERE bank_id = @BankId)", pattern);
            await connection.ExecuteAsync("DELETE FROM ap_bill WHERE vendor_id IN (SELECT id FROM vendor WHERE name LIKE @Pattern)", pattern);
            await connection.ExecuteAsync("DELETE FROM vendor WHERE name LIKE @Pattern", pattern);
            await connection.ExecuteAsync("DELETE FROM bank_account WHERE bank_id = @BankId", pattern);
            await connection.ExecuteAsync("DELETE FROM bank WHERE id = @BankId", pattern);
        }

        private async Task<long> InsertBillAsync(IDbConnection connection, string suffix) =>
            await connection.ExecuteScalarAsync<long>(
                @"INSERT INTO ap_bill (number, vendor_id, amount, paid, balance, status, created_by)
                  VALUES (@Number, @VendorId, 4000, 0, 4000, 'unpaid', 'itest') RETURNING id",
                new { Number = $"AP-{_marker}-{suffix}", VendorId = _vendorId });

        private static (IDbConnection Connection, IDbTransaction Tx) Begin()
        {
            var connection = _repositoryContext.GetDatabaseConnection();
            if (connection.State != ConnectionState.Open)
                connection.Open();
            return (connection, connection.BeginTransaction());
        }

        private Task<long> InsertTransactionRowAsync(IDbConnection connection, IDbTransaction? tx = null) =>
            connection.ExecuteScalarAsync<long>(
                @"INSERT INTO ""transaction"" (bank_account_id, type, direction, number, date, amount, status, source_number, created_by)
                  VALUES (@AccountId, 'BankPayment', 'out'::direction_enum, @Number, CURRENT_DATE, 1500, 'posted', 'AP-TEST', 'system')
                  RETURNING id",
                new { AccountId = _accountId, Number = $"{_marker}-{Guid.NewGuid():N}".Substring(0, 30) }, tx);

        private BankPayment NewPayment(long transactionId, long? billId = null, string method = "Check", string? reference = "CHK-77") => new()
        {
            TransactionId = transactionId,
            BankAccountId = _accountId,
            VendorId = _vendorId,
            ApBillId = billId ?? _billId,
            PaymentDate = new DateTime(2026, 11, 8, 9, 15, 0),
            PaymentMethod = method,
            Reference = reference,
            Amount = 1500m,
            Status = "Posted"
        };

        // A committed payment (with its own committed transaction) for the read tests.
        private async Task<long> InsertCommittedPaymentAsync(long billId, string number, string date, decimal amount)
        {
            using IDbConnection connection = _repositoryContext.GetDatabaseConnection();
            var transactionId = await InsertTransactionRowAsync(connection);
            return await connection.ExecuteScalarAsync<long>(
                @"INSERT INTO bank_payment (transaction_id, bank_account_id, vendor_id, ap_bill_id, payment_number, payment_date,
                                            payment_method, reference, amount, status, created_by)
                  VALUES (@TransactionId, @AccountId, @VendorId, @BillId, @Number, @Date::date, 'Transfer', @Reference, @Amount, 'Posted', 'itest')
                  RETURNING id",
                new
                {
                    TransactionId = transactionId, AccountId = _accountId, VendorId = _vendorId, BillId = billId,
                    Number = number, Date = date, Reference = $"REF-{number}", Amount = amount
                });
        }

        // ---- InsertBankPayment ----

        [Fact]
        public async Task InsertBankPayment_StoresEveryColumn()
        {
            var (connection, tx) = Begin();
            using (connection) using (tx)
            {
                var transactionId = await InsertTransactionRowAsync(connection, tx);

                var id = await _repository.InsertBankPayment(NewPayment(transactionId), tx);

                Assert.True(id > 0);
                var row = await connection.QuerySingleAsync<PaymentRow>(PaymentSelect, new { Id = id }, tx);
                Assert.Equal(transactionId, row.Transaction_Id);
                Assert.Equal(_accountId, row.Bank_Account_Id);
                Assert.Equal(_vendorId, row.Vendor_Id);
                Assert.Equal(_billId, row.Ap_Bill_Id);
                Assert.Null(row.Vehicle_Id);
                Assert.Equal(new DateTime(2026, 11, 8), row.Payment_Date);
                Assert.Equal("Check", row.Payment_Method);
                Assert.Equal("CHK-77", row.Reference);
                Assert.Equal(1500m, row.Amount);
                Assert.Equal("Posted", row.Status);
                Assert.Equal("system", row.Created_By);
            }
        }

        [Fact]
        public async Task InsertBankPayment_NumberIsPayYearPlusIdPaddedToSixDigits()
        {
            var (connection, tx) = Begin();
            using (connection) using (tx)
            {
                var transactionId = await InsertTransactionRowAsync(connection, tx);

                var id = await _repository.InsertBankPayment(NewPayment(transactionId), tx);

                var number = await connection.ExecuteScalarAsync<string>(
                    "SELECT payment_number FROM bank_payment WHERE id = @Id", new { Id = id }, tx);
                Assert.Equal($"PAY-{DateTime.UtcNow.Year}-{id:D6}", number);
            }
        }

        [Fact]
        public async Task InsertBankPayment_WithoutReference_StoresNull()
        {
            var (connection, tx) = Begin();
            using (connection) using (tx)
            {
                var transactionId = await InsertTransactionRowAsync(connection, tx);

                var id = await _repository.InsertBankPayment(NewPayment(transactionId, reference: null), tx);

                Assert.Null(await connection.ExecuteScalarAsync<string?>(
                    "SELECT reference FROM bank_payment WHERE id = @Id", new { Id = id }, tx));
            }
        }

        [Theory]
        [InlineData("Cash")]
        [InlineData("Card")]
        [InlineData("Transfer")]
        [InlineData("Check")]
        public async Task InsertBankPayment_StoresThePaymentMethodAsGiven(string method)
        {
            var (connection, tx) = Begin();
            using (connection) using (tx)
            {
                var transactionId = await InsertTransactionRowAsync(connection, tx);

                var id = await _repository.InsertBankPayment(NewPayment(transactionId, method: method), tx);

                Assert.Equal(method, await connection.ExecuteScalarAsync<string>(
                    "SELECT payment_method FROM bank_payment WHERE id = @Id", new { Id = id }, tx));
            }
        }

        // AC 3, 7: every installment is a new row; earlier rows stay as they were.
        [Fact]
        public async Task InsertBankPayment_SecondInstallmentForTheSameBill_AddsANewRowAndKeepsTheFirst()
        {
            var (connection, tx) = Begin();
            using (connection) using (tx)
            {
                var firstTransaction = await InsertTransactionRowAsync(connection, tx);
                var secondTransaction = await InsertTransactionRowAsync(connection, tx);
                var first = NewPayment(firstTransaction);
                var second = NewPayment(secondTransaction, reference: "CHK-78");
                second.Amount = 2000m;

                var firstId = await _repository.InsertBankPayment(first, tx);
                var secondId = await _repository.InsertBankPayment(second, tx);

                Assert.NotEqual(firstId, secondId);
                Assert.Equal(2, await connection.ExecuteScalarAsync<int>(
                    "SELECT COUNT(*) FROM bank_payment WHERE ap_bill_id = @Id", new { Id = _billId }, tx));
                var firstRow = await connection.QuerySingleAsync<PaymentRow>(PaymentSelect, new { Id = firstId }, tx);
                Assert.Equal(1500m, firstRow.Amount);
                Assert.Equal("CHK-77", firstRow.Reference);
                Assert.Equal(firstTransaction, firstRow.Transaction_Id);
                var secondRow = await connection.QuerySingleAsync<PaymentRow>(PaymentSelect, new { Id = secondId }, tx);
                Assert.Equal(2000m, secondRow.Amount);
                Assert.NotEqual(firstRow.Payment_Number, secondRow.Payment_Number);
            }
        }

        [Fact]
        public async Task InsertBankPayment_NotCommitted_LeavesNoRow()
        {
            var (connection, tx) = Begin();
            using (connection)
            {
                using (tx)
                {
                    var transactionId = await InsertTransactionRowAsync(connection, tx);
                    await _repository.InsertBankPayment(NewPayment(transactionId), tx);
                    tx.Rollback();
                }

                Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
                    "SELECT COUNT(*) FROM bank_payment WHERE ap_bill_id = @Id", new { Id = _billId }));
            }
        }

        [Fact]
        public async Task InsertBankPayment_SecondPaymentForTheSameTransaction_IsRejected()
        {
            var (connection, tx) = Begin();
            using (connection) using (tx)
            {
                var transactionId = await InsertTransactionRowAsync(connection, tx);
                await _repository.InsertBankPayment(NewPayment(transactionId), tx);

                await Assert.ThrowsAnyAsync<Exception>(() => _repository.InsertBankPayment(NewPayment(transactionId), tx));
            }
        }

        [Fact]
        public async Task InsertBankPayment_UnknownTransaction_IsRejected()
        {
            var (connection, tx) = Begin();
            using (connection) using (tx)
            {
                await Assert.ThrowsAnyAsync<Exception>(() => _repository.InsertBankPayment(NewPayment(long.MaxValue), tx));
            }
        }

        [Fact]
        public async Task InsertBankPayment_UnknownBill_IsRejected()
        {
            var (connection, tx) = Begin();
            using (connection) using (tx)
            {
                var transactionId = await InsertTransactionRowAsync(connection, tx);

                await Assert.ThrowsAnyAsync<Exception>(
                    () => _repository.InsertBankPayment(NewPayment(transactionId, long.MaxValue), tx));
            }
        }

        // ---- GetPaymentsByBillId ----

        [Fact]
        public async Task GetPaymentsByBillId_ReturnsEveryColumnOfTheBillsPayments()
        {
            var id = await InsertCommittedPaymentAsync(_billId, $"PAY-{_marker}-1", "2026-11-08", 1500m);

            var payments = (await _repository.GetPaymentsByBillId(_billId)).ToList();

            var payment = Assert.Single(payments);
            Assert.Equal(id, payment.Id);
            Assert.True(payment.TransactionId > 0);
            Assert.Equal(_accountId, payment.BankAccountId);
            Assert.Equal(_vendorId, payment.VendorId);
            Assert.Equal(_billId, payment.ApBillId);
            Assert.Null(payment.VehicleId);
            Assert.Equal($"PAY-{_marker}-1", payment.PaymentNumber);
            Assert.Equal(new DateTime(2026, 11, 8), payment.PaymentDate);
            Assert.Equal("Transfer", payment.PaymentMethod);
            Assert.Equal($"REF-PAY-{_marker}-1", payment.Reference);
            Assert.Equal(1500m, payment.Amount);
            Assert.Equal("Posted", payment.Status);
        }

        [Fact]
        public async Task GetPaymentsByBillId_OrdersByPaymentDateThenPaymentNumber()
        {
            await InsertCommittedPaymentAsync(_billId, $"PAY-{_marker}-3", "2026-12-01", 3m);
            await InsertCommittedPaymentAsync(_billId, $"PAY-{_marker}-2", "2026-11-15", 2m);
            await InsertCommittedPaymentAsync(_billId, $"PAY-{_marker}-1", "2026-11-15", 1m);

            var payments = (await _repository.GetPaymentsByBillId(_billId)).ToList();

            Assert.Equal(new[] { 1m, 2m, 3m }, payments.Select(p => p.Amount).ToArray());
        }

        [Fact]
        public async Task GetPaymentsByBillId_ReturnsOnlyThePaymentsOfThatBill()
        {
            await InsertCommittedPaymentAsync(_billId, $"PAY-{_marker}-1", "2026-11-08", 100m);
            await InsertCommittedPaymentAsync(_otherBillId, $"PAY-{_marker}-2", "2026-11-08", 200m);

            var payments = (await _repository.GetPaymentsByBillId(_billId)).ToList();

            Assert.Equal(new[] { 100m }, payments.Select(p => p.Amount).ToArray());
        }

        [Fact]
        public async Task GetPaymentsByBillId_NoPayments_ReturnsEmptyList()
        {
            var payments = await _repository.GetPaymentsByBillId(_billId);

            Assert.NotNull(payments);
            Assert.Empty(payments);
        }

        [Fact]
        public async Task GetPaymentsByBillId_UnknownBill_ReturnsEmptyList()
        {
            Assert.Empty(await _repository.GetPaymentsByBillId(long.MaxValue));
        }

        // ---- Story 21: RecordMiscellaneousPayment (committed writes; everything is removed in DisposeAsync) ----

        private MiscellaneousPaymentCreateDto Misc(decimal amount = 1000m, long? vendorId = null, long? accountId = null,
            string method = "Transfer") => new()
        {
            Payee = "City Landlord", Payment_Category = "Rent", Vendor_Id = vendorId,
            Payment_Date = new DateTime(2026, 11, 8), Amount = amount, Payment_Method = method,
            Bank_Account_Id = accountId ?? _accountId
        };

        private async Task<(int Transactions, int Lines, int Payments, decimal Book)> CountsAsync(long? accountId = null)
        {
            using IDbConnection connection = _repositoryContext.GetDatabaseConnection();
            var id = new { Id = accountId ?? _accountId };
            return (
                await connection.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM ""transaction"" WHERE bank_account_id = @Id", id),
                await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM bank_statement_line WHERE bank_account_id = @Id", id),
                await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM bank_payment WHERE bank_account_id = @Id", id),
                await connection.ExecuteScalarAsync<decimal>("SELECT book_balance FROM bank_account WHERE id = @Id", id));
        }

        private async Task<long> AddAccountAsync(string status, decimal balance)
        {
            using IDbConnection connection = _repositoryContext.GetDatabaseConnection();
            return await connection.ExecuteScalarAsync<long>(
                @"INSERT INTO bank_account (bank_id, name, type, opening_balance, book_balance, status)
                  VALUES (@BankId, @Name, 'Checking', @Balance, @Balance, @Status) RETURNING id",
                new { BankId = _bankId, Name = $"{_marker} {status}", Balance = balance, Status = status });
        }

        private class TxnRow
        {
            public string Type { get; set; } = "";
            public string Direction { get; set; } = "";
            public string? Party { get; set; }
            public decimal Amount { get; set; }
            public string Status { get; set; } = "";
            public string Source_Number { get; set; } = "";
            public DateTime Date { get; set; }
            public string? Description { get; set; }
        }

        private class LineRow
        {
            public decimal Deposit { get; set; }
            public decimal Withdrawal { get; set; }
            public decimal Running_Balance { get; set; }
            public string Direction { get; set; } = "";
            public string Type { get; set; } = "";
            public string? Party { get; set; }
            public string Statement_Status { get; set; } = "";
            public string Match_Status { get; set; } = "";
            public string Source { get; set; } = "";
        }

        private class MiscPaymentRow
        {
            public long Transaction_Id { get; set; }
            public long? Vendor_Id { get; set; }
            public long? Ap_Bill_Id { get; set; }
            public long? Vehicle_Id { get; set; }
            public string Payment_Number { get; set; } = "";
            public string Payment_Method { get; set; } = "";
            public string? Reference { get; set; }
            public decimal Amount { get; set; }
            public string? Payee { get; set; }
            public string? Payment_Category { get; set; }
            public string? Description { get; set; }
            public string Status { get; set; } = "";
            public string Created_By { get; set; } = "";
        }

        [Fact]
        public async Task RecordMiscellaneousPayment_ReturnsTheSavedPaymentAndNewBookBalance()
        {
            var result = await _repository.RecordMiscellaneousPayment(Misc(1000m));

            Assert.True(result.Id > 0);
            Assert.True(result.Transaction_Id > 0);
            Assert.Equal($"PAY-{DateTime.UtcNow.Year}-{result.Id:D6}", result.Payment_Number);
            Assert.Equal("City Landlord", result.Payee);
            Assert.Equal("Rent", result.Payment_Category);
            Assert.Equal(new DateTime(2026, 11, 8), result.Payment_Date);
            Assert.Equal(1000m, result.Amount);
            Assert.Equal("Transfer", result.Payment_Method);
            Assert.Equal(_accountId, result.Bank_Account_Id);
            Assert.Equal(4000m, result.Book_Balance);
            Assert.Equal("Posted", result.Status);
        }

        [Fact]
        public async Task RecordMiscellaneousPayment_DecreasesTheBookBalance()
        {
            await _repository.RecordMiscellaneousPayment(Misc(1250.50m));

            Assert.Equal(3749.50m, (await CountsAsync()).Book);
        }

        [Fact]
        public async Task RecordMiscellaneousPayment_CreatesExactlyOneOutTransaction()
        {
            var result = await _repository.RecordMiscellaneousPayment(Misc(1000m));

            Assert.Equal(1, (await CountsAsync()).Transactions);
            using IDbConnection connection = _repositoryContext.GetDatabaseConnection();
            var txn = await connection.QuerySingleAsync<TxnRow>(
                @"SELECT type, direction::text AS direction, party, amount, status, source_number, date::timestamp AS date, description
                  FROM ""transaction"" WHERE id = @Id", new { Id = result.Transaction_Id });
            Assert.Equal("BankPayment", txn.Type);
            Assert.Equal("out", txn.Direction);
            Assert.Equal("City Landlord", txn.Party);
            Assert.Equal(1000m, txn.Amount);
            Assert.Equal("posted", txn.Status);
            Assert.Equal(result.Payment_Number, txn.Source_Number);
            Assert.Equal(new DateTime(2026, 11, 8), txn.Date);
            Assert.Equal("Payment to City Landlord (Rent)", txn.Description);
        }

        [Fact]
        public async Task RecordMiscellaneousPayment_CreatesExactlyOneWithdrawalStatementLine()
        {
            var result = await _repository.RecordMiscellaneousPayment(Misc(1000m));

            Assert.Equal(1, (await CountsAsync()).Lines);
            using IDbConnection connection = _repositoryContext.GetDatabaseConnection();
            var line = await connection.QuerySingleAsync<LineRow>(
                @"SELECT deposit, withdrawal, running_balance, direction::text AS direction, type, party,
                         statement_status, match_status, source
                  FROM bank_statement_line WHERE transaction_id = @Id", new { Id = result.Transaction_Id });
            Assert.Equal(0m, line.Deposit);
            Assert.Equal(1000m, line.Withdrawal);
            Assert.Equal(4000m, line.Running_Balance);
            Assert.Equal("out", line.Direction);
            Assert.Equal("BankPayment", line.Type);
            Assert.Equal("City Landlord", line.Party);
            Assert.Equal("Cleared", line.Statement_Status);
            Assert.Equal("Matched", line.Match_Status);
            Assert.Equal("Simulated", line.Source);
        }

        [Fact]
        public async Task RecordMiscellaneousPayment_RunningBalanceContinuesFromThePreviousLine()
        {
            await _repository.RecordMiscellaneousPayment(Misc(1000m));
            var second = await _repository.RecordMiscellaneousPayment(Misc(500m));

            using IDbConnection connection = _repositoryContext.GetDatabaseConnection();
            Assert.Equal(3500m, await connection.ExecuteScalarAsync<decimal>(
                "SELECT running_balance FROM bank_statement_line WHERE transaction_id = @Id", new { Id = second.Transaction_Id }));
        }

        [Fact]
        public async Task RecordMiscellaneousPayment_SavesAPaymentNotLinkedToApOrVehicle()
        {
            var payment = Misc(1000m);
            payment.Reference = "CHK-77";
            payment.Description = "November rent";
            var result = await _repository.RecordMiscellaneousPayment(payment);

            using IDbConnection connection = _repositoryContext.GetDatabaseConnection();
            var row = await connection.QuerySingleAsync<MiscPaymentRow>(
                @"SELECT transaction_id, vendor_id, ap_bill_id, vehicle_id, payment_number, payment_method, reference, amount,
                         payee, payment_category, description, status, created_by
                  FROM bank_payment WHERE id = @Id", new { Id = result.Id });
            Assert.Equal(result.Transaction_Id, row.Transaction_Id);
            Assert.Null(row.Ap_Bill_Id);
            Assert.Null(row.Vehicle_Id);
            Assert.Null(row.Vendor_Id);
            Assert.Equal(result.Payment_Number, row.Payment_Number);
            Assert.Equal("Transfer", row.Payment_Method);
            Assert.Equal("CHK-77", row.Reference);
            Assert.Equal(1000m, row.Amount);
            Assert.Equal("City Landlord", row.Payee);
            Assert.Equal("Rent", row.Payment_Category);
            Assert.Equal("November rent", row.Description);
            Assert.Equal("Posted", row.Status);
            Assert.Equal("system", row.Created_By);
        }

        [Fact]
        public async Task RecordMiscellaneousPayment_UsesTheGivenDescriptionOnTheTransactionAndTheLine()
        {
            var payment = Misc(1000m);
            payment.Description = "November rent";
            var result = await _repository.RecordMiscellaneousPayment(payment);

            using IDbConnection connection = _repositoryContext.GetDatabaseConnection();
            Assert.Equal("November rent", await connection.ExecuteScalarAsync<string>(
                @"SELECT description FROM ""transaction"" WHERE id = @Id", new { Id = result.Transaction_Id }));
            Assert.Equal("November rent", await connection.ExecuteScalarAsync<string>(
                "SELECT description FROM bank_statement_line WHERE transaction_id = @Id", new { Id = result.Transaction_Id }));
        }

        [Fact]
        public async Task RecordMiscellaneousPayment_NoReferenceOrDescription_StoresNulls()
        {
            var result = await _repository.RecordMiscellaneousPayment(Misc(1000m));

            using IDbConnection connection = _repositoryContext.GetDatabaseConnection();
            var row = await connection.QuerySingleAsync<MiscPaymentRow>(
                "SELECT reference, description FROM bank_payment WHERE id = @Id", new { Id = result.Id });
            Assert.Null(row.Reference);
            Assert.Null(row.Description);
            Assert.Null(result.Reference);
            Assert.Null(result.Description);
        }

        [Fact]
        public async Task RecordMiscellaneousPayment_TrimsTextFields()
        {
            var payment = Misc(1000m);
            payment.Payee = "  City Landlord ";
            payment.Payment_Category = " Rent  ";
            payment.Reference = " CHK-1 ";
            var result = await _repository.RecordMiscellaneousPayment(payment);

            Assert.Equal("City Landlord", result.Payee);
            Assert.Equal("Rent", result.Payment_Category);
            Assert.Equal("CHK-1", result.Reference);
        }

        [Theory]
        [InlineData("cash", "Cash")]
        [InlineData("CHECK", "Check")]
        [InlineData(" card ", "Card")]
        [InlineData("Transfer", "Transfer")]
        public async Task RecordMiscellaneousPayment_PaymentMethod_AnyCasingStoredAsTheStandardSpelling(string given, string stored)
        {
            var result = await _repository.RecordMiscellaneousPayment(Misc(100m, method: given));

            Assert.Equal(stored, result.Payment_Method);
        }

        [Fact]
        public async Task RecordMiscellaneousPayment_WithVendor_LinksTheVendorButLeavesItsBalance()
        {
            using (IDbConnection connection = _repositoryContext.GetDatabaseConnection())
                await connection.ExecuteAsync("UPDATE vendor SET balance = 700 WHERE id = @Id", new { Id = _vendorId });

            var result = await _repository.RecordMiscellaneousPayment(Misc(1000m, vendorId: _vendorId));

            Assert.Equal(_vendorId, result.Vendor_Id);
            using (IDbConnection connection = _repositoryContext.GetDatabaseConnection())
            {
                Assert.Equal(_vendorId, await connection.ExecuteScalarAsync<long>(
                    "SELECT vendor_id FROM bank_payment WHERE id = @Id", new { Id = result.Id }));
                Assert.Equal(700m, await connection.ExecuteScalarAsync<decimal>(
                    "SELECT balance FROM vendor WHERE id = @Id", new { Id = _vendorId }));
            }
        }

        [Fact]
        public async Task RecordMiscellaneousPayment_AmountEqualToTheBalance_IsAllowed()
        {
            var result = await _repository.RecordMiscellaneousPayment(Misc(5000m));

            Assert.Equal(0m, result.Book_Balance);
            Assert.Equal(0m, (await CountsAsync()).Book);
        }

        [Fact]
        public async Task RecordMiscellaneousPayment_AmountAboveTheBalance_IsRejectedAndNothingIsSaved()
        {
            var ex = await Assert.ThrowsAsync<System.ComponentModel.DataAnnotations.ValidationException>(
                () => _repository.RecordMiscellaneousPayment(Misc(5000.01m)));

            Assert.Equal("Amount cannot exceed the bank account's book balance.", ex.Message);
            Assert.Equal((0, 0, 0, 5000m), await CountsAsync());
        }

        [Fact]
        public async Task RecordMiscellaneousPayment_InactiveAccount_IsRejectedAndNothingIsSaved()
        {
            var inactiveId = await AddAccountAsync("inactive", 5000m);

            var ex = await Assert.ThrowsAsync<System.ComponentModel.DataAnnotations.ValidationException>(
                () => _repository.RecordMiscellaneousPayment(Misc(100m, accountId: inactiveId)));

            Assert.Equal("The bank account must be active.", ex.Message);
            Assert.Equal((0, 0, 0, 5000m), await CountsAsync(inactiveId));
        }

        [Fact]
        public async Task RecordMiscellaneousPayment_UnknownAccount_IsRejected()
        {
            var ex = await Assert.ThrowsAsync<System.ComponentModel.DataAnnotations.ValidationException>(
                () => _repository.RecordMiscellaneousPayment(Misc(100m, accountId: long.MaxValue)));

            Assert.Equal("An existing bank account is required.", ex.Message);
        }

        [Fact]
        public async Task RecordMiscellaneousPayment_UnknownVendor_IsRejectedAndNothingIsSaved()
        {
            var ex = await Assert.ThrowsAsync<System.ComponentModel.DataAnnotations.ValidationException>(
                () => _repository.RecordMiscellaneousPayment(Misc(100m, vendorId: long.MaxValue)));

            Assert.Equal("The vendor does not exist.", ex.Message);
            Assert.Equal((0, 0, 0, 5000m), await CountsAsync());
        }

        [Theory]
        [InlineData("Cheque")]
        [InlineData("")]
        [InlineData("Bitcoin")]
        public async Task RecordMiscellaneousPayment_InvalidMethod_IsRejectedAndNothingIsSaved(string method)
        {
            var ex = await Assert.ThrowsAsync<System.ComponentModel.DataAnnotations.ValidationException>(
                () => _repository.RecordMiscellaneousPayment(Misc(100m, method: method)));

            Assert.Equal("Payment method must be Cash, Check, Card or Transfer.", ex.Message);
            Assert.Equal((0, 0, 0, 5000m), await CountsAsync());
        }

        [Fact]
        public async Task RecordMiscellaneousPayment_ErrorAfterTheTransactionIsWritten_RollsEverythingBack()
        {
            var payment = Misc(1000m);
            payment.Reference = new string('x', 101); // bank_payment.reference is VARCHAR(100): fails on the 4th insert

            await Assert.ThrowsAnyAsync<Exception>(() => _repository.RecordMiscellaneousPayment(payment));

            Assert.Equal((0, 0, 0, 5000m), await CountsAsync());
        }

        [Fact]
        public async Task RecordMiscellaneousPayment_TwoPayments_AreSeparateAndTheFirstIsNeverOverwritten()
        {
            var first = await _repository.RecordMiscellaneousPayment(Misc(1000m));
            var second = await _repository.RecordMiscellaneousPayment(Misc(500m));

            Assert.NotEqual(first.Id, second.Id);
            Assert.NotEqual(first.Payment_Number, second.Payment_Number);
            Assert.NotEqual(first.Transaction_Id, second.Transaction_Id);
            Assert.Equal((2, 2, 2, 3500m), await CountsAsync());
            using IDbConnection connection = _repositoryContext.GetDatabaseConnection();
            Assert.Equal(1000m, await connection.ExecuteScalarAsync<decimal>(
                "SELECT amount FROM bank_payment WHERE id = @Id", new { Id = first.Id }));
        }

        [Fact]
        public async Task RecordMiscellaneousPayment_DoesNotTouchOtherAccountsOrApPayments()
        {
            var otherId = await AddAccountAsync("active", 900m);

            await _repository.RecordMiscellaneousPayment(Misc(1000m));

            Assert.Equal((0, 0, 0, 900m), await CountsAsync(otherId));
            using IDbConnection connection = _repositoryContext.GetDatabaseConnection();
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM bank_payment WHERE ap_bill_id IN (@A, @B)", new { A = _billId, B = _otherBillId }));
        }
    }
}
