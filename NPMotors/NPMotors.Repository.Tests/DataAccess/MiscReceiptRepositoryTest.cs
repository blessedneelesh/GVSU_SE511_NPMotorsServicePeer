using System.Data;
using Dapper;
using NPMotors.Repository.Models;
using NPMotors.Repository.Models.Enums;
using NPMotors.Repository.Repositories;

namespace NPMotors.Repository.Tests.DataAccess
{
    // Story 16: BankReceiptRepository.InsertBankReceipt for a receipt not linked to an invoice
    // (null customer, sales invoice and AR invoice; payer and description set).
    // Every insert runs inside a transaction that is never committed, so nothing stays in the database.
    public class MiscReceiptRepositoryTest : TestBase
    {
        private readonly BankReceiptRepository _receiptRepository;
        private readonly string _marker = $"itest-{Guid.NewGuid():N}".Substring(0, 12);

        public MiscReceiptRepositoryTest()
        {
            _receiptRepository = new BankReceiptRepository(_repositoryContext);
        }

        private class ReceiptRow
        {
            public long Transaction_Id { get; set; }
            public long Bank_Account_Id { get; set; }
            public long? Customer_Id { get; set; }
            public long? Sales_Invoice_Id { get; set; }
            public long? Ar_Invoice_Id { get; set; }
            public string Receipt_Number { get; set; } = "";
            public DateTime Receipt_Date { get; set; }
            public string Payment_Method { get; set; } = "";
            public string? Reference { get; set; }
            public string? Payer { get; set; }
            public string? Description { get; set; }
            public decimal Amount { get; set; }
            public string Status { get; set; } = "";
            public string Created_By { get; set; } = "";
        }

        private const string SelectReceipt =
            @"SELECT transaction_id, bank_account_id, customer_id, sales_invoice_id, ar_invoice_id, receipt_number,
                     receipt_date, payment_method, reference, payer, description, amount, status, created_by
              FROM bank_receipt WHERE id = @Id";

        private static (IDbConnection Connection, IDbTransaction Tx) Begin()
        {
            var connection = _repositoryContext.GetDatabaseConnection();
            if (connection.State != ConnectionState.Open)
                connection.Open();
            return (connection, connection.BeginTransaction());
        }

        private async Task<long> InsertAccountAsync(IDbTransaction tx)
        {
            var connection = tx.Connection!;
            var bankId = await connection.ExecuteScalarAsync<long>(
                "INSERT INTO bank (name) VALUES (@Name) RETURNING id", new { Name = $"{_marker} Bank" }, tx);
            return await connection.ExecuteScalarAsync<long>(
                @"INSERT INTO bank_account (bank_id, name, type, opening_balance, book_balance, status)
                  VALUES (@BankId, @Name, 'Checking', 0, 0, 'active') RETURNING id",
                new { BankId = bankId, Name = $"{_marker} Account" }, tx);
        }

        private async Task<long> InsertTransactionAsync(IDbTransaction tx, long accountId) =>
            await tx.Connection!.ExecuteScalarAsync<long>(
                @"INSERT INTO ""transaction"" (bank_account_id, type, direction, number, date, party, amount, status, source_number, created_by)
                  VALUES (@AccountId, 'BankReceipt', 'in'::direction_enum, @Number, CURRENT_DATE, 'Acme Corp', 750.50, 'posted', 'BankReceipt', 'system')
                  RETURNING id",
                new { AccountId = accountId, Number = $"{_marker}-{Guid.NewGuid():N}".Substring(0, 30) }, tx);

        private static BankReceipt NewMiscReceipt(long transactionId, long accountId, string payer = "Acme Corp",
            decimal amount = 750.50m, string? reference = "CHK-55") => new()
        {
            TransactionId = transactionId,
            BankAccountId = accountId,
            CustomerId = null,
            SalesInvoiceId = null,
            ArInvoiceId = null,
            ReceiptDate = new DateTime(2026, 11, 8, 9, 15, 0),
            PaymentMethod = PaymentMethod.Check,
            Reference = reference,
            Payer = payer,
            Description = "Scrap metal sale",
            Amount = amount,
            Status = "Posted",
            CreatedAt = new DateTime(2026, 11, 8, 9, 15, 0),
            CreatedBy = "system"
        };

        [Fact]
        public async Task InsertBankReceipt_UnlinkedReceipt_StoresEveryColumnWithNullLinks()
        {
            var (connection, tx) = Begin();
            using (connection) using (tx)
            {
                var accountId = await InsertAccountAsync(tx);
                var transactionId = await InsertTransactionAsync(tx, accountId);

                var id = await _receiptRepository.InsertBankReceipt(NewMiscReceipt(transactionId, accountId), tx);

                Assert.True(id > 0);
                var row = await connection.QuerySingleAsync<ReceiptRow>(SelectReceipt, new { Id = id }, tx);
                Assert.Equal(transactionId, row.Transaction_Id);
                Assert.Equal(accountId, row.Bank_Account_Id);
                Assert.Null(row.Customer_Id);
                Assert.Null(row.Sales_Invoice_Id);
                Assert.Null(row.Ar_Invoice_Id);
                Assert.Equal(new DateTime(2026, 11, 8), row.Receipt_Date);
                Assert.Equal("Check", row.Payment_Method);
                Assert.Equal("CHK-55", row.Reference);
                Assert.Equal("Acme Corp", row.Payer);
                Assert.Equal("Scrap metal sale", row.Description);
                Assert.Equal(750.50m, row.Amount);
                Assert.Equal("Posted", row.Status);
                Assert.Equal("system", row.Created_By);
            }
        }

        [Fact]
        public async Task InsertBankReceipt_UnlinkedReceipt_NumberIsRctYearPlusIdPaddedToSixDigits()
        {
            var (connection, tx) = Begin();
            using (connection) using (tx)
            {
                var accountId = await InsertAccountAsync(tx);
                var transactionId = await InsertTransactionAsync(tx, accountId);

                var id = await _receiptRepository.InsertBankReceipt(NewMiscReceipt(transactionId, accountId), tx);

                var number = await connection.ExecuteScalarAsync<string>(
                    "SELECT receipt_number FROM bank_receipt WHERE id = @Id", new { Id = id }, tx);
                Assert.Equal($"RCT-2026-{id:D6}", number);
            }
        }

        [Fact]
        public async Task InsertBankReceipt_NullReference_StoresNull()
        {
            var (connection, tx) = Begin();
            using (connection) using (tx)
            {
                var accountId = await InsertAccountAsync(tx);
                var transactionId = await InsertTransactionAsync(tx, accountId);

                var id = await _receiptRepository.InsertBankReceipt(NewMiscReceipt(transactionId, accountId, reference: null), tx);

                var row = await connection.QuerySingleAsync<ReceiptRow>(SelectReceipt, new { Id = id }, tx);
                Assert.Null(row.Reference);
            }
        }

        [Fact]
        public async Task InsertBankReceipt_TwoUnlinkedReceipts_AreTwoSeparateRowsAndTheFirstIsNeverChanged()
        {
            var (connection, tx) = Begin();
            using (connection) using (tx)
            {
                var accountId = await InsertAccountAsync(tx);
                var firstTransaction = await InsertTransactionAsync(tx, accountId);
                var secondTransaction = await InsertTransactionAsync(tx, accountId);

                var firstId = await _receiptRepository.InsertBankReceipt(
                    NewMiscReceipt(firstTransaction, accountId, "Acme Corp", 100m, "R-1"), tx);
                var secondId = await _receiptRepository.InsertBankReceipt(
                    NewMiscReceipt(secondTransaction, accountId, "Bolt Ltd", 200m, "R-2"), tx);

                Assert.NotEqual(firstId, secondId);
                Assert.Equal(2, await connection.ExecuteScalarAsync<int>(
                    "SELECT COUNT(*) FROM bank_receipt WHERE bank_account_id = @Id", new { Id = accountId }, tx));
                var first = await connection.QuerySingleAsync<ReceiptRow>(SelectReceipt, new { Id = firstId }, tx);
                Assert.Equal(firstTransaction, first.Transaction_Id);
                Assert.Equal("Acme Corp", first.Payer);
                Assert.Equal(100m, first.Amount);
                Assert.Equal("R-1", first.Reference);
                var second = await connection.QuerySingleAsync<ReceiptRow>(SelectReceipt, new { Id = secondId }, tx);
                Assert.Equal("Bolt Ltd", second.Payer);
                Assert.Equal(200m, second.Amount);
            }
        }

        [Fact]
        public async Task InsertBankReceipt_SamePayerAndAmountTwice_AreTwoSeparateRows()
        {
            var (connection, tx) = Begin();
            using (connection) using (tx)
            {
                var accountId = await InsertAccountAsync(tx);
                var firstId = await _receiptRepository.InsertBankReceipt(
                    NewMiscReceipt(await InsertTransactionAsync(tx, accountId), accountId), tx);
                var secondId = await _receiptRepository.InsertBankReceipt(
                    NewMiscReceipt(await InsertTransactionAsync(tx, accountId), accountId), tx);

                Assert.NotEqual(firstId, secondId);
                Assert.Equal(2, await connection.ExecuteScalarAsync<int>(
                    "SELECT COUNT(*) FROM bank_receipt WHERE bank_account_id = @Id", new { Id = accountId }, tx));
            }
        }

        [Fact]
        public async Task InsertBankReceipt_SameTransactionTwice_IsRejected()
        {
            var (connection, tx) = Begin();
            using (connection) using (tx)
            {
                var accountId = await InsertAccountAsync(tx);
                var transactionId = await InsertTransactionAsync(tx, accountId);
                await _receiptRepository.InsertBankReceipt(NewMiscReceipt(transactionId, accountId), tx);

                // One Transaction belongs to exactly one receipt (transaction_id is UNIQUE).
                await Assert.ThrowsAnyAsync<Exception>(
                    () => _receiptRepository.InsertBankReceipt(NewMiscReceipt(transactionId, accountId), tx));
            }
        }

        [Fact]
        public async Task InsertBankReceipt_NoPayerAndNoInvoiceLink_IsRejectedByTheDatabase()
        {
            var (connection, tx) = Begin();
            using (connection) using (tx)
            {
                var accountId = await InsertAccountAsync(tx);
                var transactionId = await InsertTransactionAsync(tx, accountId);
                var receipt = NewMiscReceipt(transactionId, accountId);
                receipt.Payer = null;

                // ck_bank_receipt_payer: a receipt needs an invoice link or a payer.
                await Assert.ThrowsAnyAsync<Exception>(() => _receiptRepository.InsertBankReceipt(receipt, tx));
            }
        }

        [Fact]
        public async Task InsertBankReceipt_RolledBack_LeavesNoReceipt()
        {
            long accountId;
            long receiptId;
            var (connection, tx) = Begin();
            using (connection)
            {
                using (tx)
                {
                    accountId = await InsertAccountAsync(tx);
                    var transactionId = await InsertTransactionAsync(tx, accountId);
                    receiptId = await _receiptRepository.InsertBankReceipt(NewMiscReceipt(transactionId, accountId), tx);
                    tx.Rollback();
                }
            }

            using var check = _repositoryContext.GetDatabaseConnection();
            Assert.Equal(0, await check.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM bank_receipt WHERE id = @Id", new { Id = receiptId }));
        }
    }
}
