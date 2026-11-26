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
    }
}
