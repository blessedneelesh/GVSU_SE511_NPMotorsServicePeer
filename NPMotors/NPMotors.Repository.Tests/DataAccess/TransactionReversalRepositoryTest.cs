using System.ComponentModel.DataAnnotations;
using Dapper;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.Repositories;

namespace NPMotors.Repository.Tests.DataAccess
{
    // Story 23: TransactionRepository.GetTransactionForReversal and ReverseTransaction.
    public class TransactionReversalRepositoryTest : TestBase, IAsyncLifetime
    {
        private readonly TransactionRepository _repository;
        private readonly ReversalSeed _seed;

        public TransactionReversalRepositoryTest()
        {
            _repository = new TransactionRepository(_repositoryContext);
            _seed = new ReversalSeed(() => _repositoryContext.GetDatabaseConnection());
        }

        public Task InitializeAsync() => _seed.InitializeAsync();

        public Task DisposeAsync() => _seed.CleanupAsync();

        private static SourceBalanceDto Document(string type, long id, decimal amount, decimal paid, decimal balance, string status) => new()
        {
            Document_Type = type, Document_Id = id, Amount = amount, Paid = paid, Balance = balance, Status = status
        };

        private ReversalPostingDto Posting(ReversalSeed.Seeded seeded, string sourceType, string direction, decimal amount,
            params SourceBalanceDto[] restored) => new()
        {
            Original_Transaction_Id = seeded.TransactionId,
            Reason = "Wrong amount",
            Direction = direction,
            Amount = amount,
            Bank_Account_Id = _seed.AccountId,
            Source_Type = sourceType,
            Source_Id = seeded.SourceId,
            Source_Number = "SRC-1",
            Restored_Documents = restored.ToList()
        };

        // The receipt of 5 000 on AR 20 000 (paid 15 000 -> 10 000 after the reversal).
        private static SourceBalanceDto[] ArRestored(ReversalSeed.Seeded s, decimal paid = 10000m, decimal balance = 10000m, string status = "Partial") => new[]
        {
            Document("ARInvoice", s.ArId!.Value, 20000m, paid, balance, status),
            Document("SalesInvoice", s.InvoiceId!.Value, 20000m, paid, balance, status)
        };

        private async Task<T> ScalarAsync<T>(string sql, object? param = null)
        {
            using var c = _seed.Open();
            return await c.ExecuteScalarAsync<T>(sql, param);
        }

        // ---- GetTransactionForReversal ----

        [Fact]
        public async Task GetTransactionForReversal_Unknown_ReturnsNull()
        {
            Assert.Null(await _repository.GetTransactionForReversal(long.MaxValue));
        }

        [Fact]
        public async Task GetTransactionForReversal_ArReceipt_ReturnsTransactionReceiptAndBothDocuments()
        {
            var s = await _seed.SeedArReceiptAsync();

            var source = await _repository.GetTransactionForReversal(s.TransactionId);

            Assert.NotNull(source);
            Assert.Equal(s.TransactionId, source!.Transaction_Id);
            Assert.Equal("BankReceipt", source.Type);
            Assert.Equal("in", source.Direction);
            Assert.Equal(5000m, source.Amount);
            Assert.Equal("posted", source.Status);
            Assert.Equal(_seed.AccountId, source.Bank_Account_Id);
            Assert.Null(source.Reverses_Transaction_Id);
            Assert.Null(source.Reversed_By_Transaction_Id);
            Assert.Equal("Receipt", source.Source_Type);
            Assert.Equal(s.SourceId, source.Source_Id);
            Assert.StartsWith("RCT-SEED-", source.Source_Number);
            Assert.Equal("Posted", source.Source_Status);

            Assert.Equal(2, source.Linked_Documents.Count);
            var ar = Assert.Single(source.Linked_Documents, d => d.Document_Type == "ARInvoice");
            Assert.Equal(s.ArId, ar.Document_Id);
            Assert.Equal(20000m, ar.Amount);
            Assert.Equal(15000m, ar.Paid);
            Assert.Equal(5000m, ar.Balance);
            Assert.Equal("partial", ar.Status);
            var invoice = Assert.Single(source.Linked_Documents, d => d.Document_Type == "SalesInvoice");
            Assert.Equal(s.InvoiceId, invoice.Document_Id);
            Assert.Equal(20000m, invoice.Amount);
            Assert.Equal(15000m, invoice.Paid);
            Assert.Equal(5000m, invoice.Balance);
        }

        [Fact]
        public async Task GetTransactionForReversal_ApPayment_ReturnsPaymentAndBill()
        {
            var s = await _seed.SeedApPaymentAsync();

            var source = await _repository.GetTransactionForReversal(s.TransactionId);

            Assert.Equal("BankPayment", source!.Type);
            Assert.Equal("out", source.Direction);
            Assert.Equal("Payment", source.Source_Type);
            Assert.Equal(s.SourceId, source.Source_Id);
            Assert.StartsWith("PAY-SEED-", source.Source_Number);
            var bill = Assert.Single(source.Linked_Documents);
            Assert.Equal("APBill", bill.Document_Type);
            Assert.Equal(s.BillId, bill.Document_Id);
            Assert.Equal(8000m, bill.Amount);
            Assert.Equal(3000m, bill.Paid);
            Assert.Equal(5000m, bill.Balance);
            Assert.Equal("partial", bill.Status);
        }

        [Fact]
        public async Task GetTransactionForReversal_MiscReceipt_HasNoLinkedDocuments()
        {
            var s = await _seed.SeedMiscReceiptAsync();

            var source = await _repository.GetTransactionForReversal(s.TransactionId);

            Assert.Equal("Receipt", source!.Source_Type);
            Assert.Empty(source.Linked_Documents);
        }

        [Fact]
        public async Task GetTransactionForReversal_TransactionWithoutReceiptOrPayment_HasEmptySourceType()
        {
            var s = await _seed.SeedPlainTransactionAsync();

            var source = await _repository.GetTransactionForReversal(s.TransactionId);

            Assert.Equal(string.Empty, source!.Source_Type);
            Assert.Equal(0, source.Source_Id);
            Assert.Empty(source.Linked_Documents);
        }

        [Fact]
        public async Task GetTransactionForReversal_AfterReversal_OriginalShowsReversedByAndReversalShowsReverses()
        {
            var s = await _seed.SeedMiscReceiptAsync();
            var reversal = await _repository.ReverseTransaction(Posting(s, "Receipt", "out", 1000m));

            var original = await _repository.GetTransactionForReversal(s.TransactionId);
            var reversalSource = await _repository.GetTransactionForReversal(reversal.Id);

            Assert.Equal(reversal.Id, original!.Reversed_By_Transaction_Id);
            Assert.Equal(s.TransactionId, reversalSource!.Reverses_Transaction_Id);
            Assert.Equal("Reversal", reversalSource.Type);
            Assert.Equal(string.Empty, reversalSource.Source_Type);
        }

        [Fact]
        public async Task GetTransactionForReversal_IsReadOnly()
        {
            var s = await _seed.SeedArReceiptAsync();

            await _repository.GetTransactionForReversal(s.TransactionId);

            Assert.Equal(1, await _seed.CountTransactionsAsync());
            Assert.Equal(50000m, await _seed.GetBookBalanceAsync());
        }

        // ---- ReverseTransaction: the reversal transaction ----

        [Fact]
        public async Task ReverseTransaction_ReceiptOnAr_InsertsOutgoingReversalThatReferencesTheOriginal()
        {
            var s = await _seed.SeedArReceiptAsync();

            var reversal = await _repository.ReverseTransaction(Posting(s, "Receipt", "out", 5000m, ArRestored(s)));

            Assert.NotEqual(s.TransactionId, reversal.Id);
            Assert.Equal($"TXN-{DateTime.UtcNow.Year}-{reversal.Id:D6}", reversal.Number);
            Assert.Equal("Reversal", reversal.Type);
            Assert.Equal("out", reversal.Direction);
            Assert.Equal(5000m, reversal.Amount);
            Assert.Equal("posted", reversal.Status);
            Assert.Equal(_seed.AccountId, reversal.Bank_Account_Id);
            Assert.Equal("SRC-1", reversal.Source_Number);
            Assert.Equal("Acme", reversal.Party);
            Assert.Equal("Wrong amount", reversal.Reversal_Reason);
            Assert.Equal(s.TransactionId, reversal.Reverses_Transaction_Id);
            Assert.Equal(DateTime.UtcNow.Date, reversal.Date);
            Assert.Equal("system", reversal.Created_By);
        }

        [Fact]
        public async Task ReverseTransaction_PaymentOnAp_InsertsIncomingReversal()
        {
            var s = await _seed.SeedApPaymentAsync();

            var reversal = await _repository.ReverseTransaction(Posting(s, "Payment", "in", 3000m,
                Document("APBill", s.BillId!.Value, 8000m, 0m, 8000m, "Unpaid")));

            Assert.Equal("Reversal", reversal.Type);
            Assert.Equal("in", reversal.Direction);
            Assert.Equal(3000m, reversal.Amount);
            Assert.Equal(s.TransactionId, reversal.Reverses_Transaction_Id);
        }

        [Fact]
        public async Task ReverseTransaction_OriginalTransactionStaysUnchangedAndVisible()
        {
            var s = await _seed.SeedArReceiptAsync();

            var reversal = await _repository.ReverseTransaction(Posting(s, "Receipt", "out", 5000m, ArRestored(s)));

            var original = await _repository.GetTransactionById(s.TransactionId);
            Assert.NotNull(original);
            Assert.Equal("in", original!.Direction);
            Assert.Equal(5000m, original.Amount);
            Assert.Equal("posted", original.Status);
            Assert.Null(original.Reverses_Transaction_Id);
            Assert.Null(original.Reversal_Reason);
            Assert.Equal(2, await _seed.CountTransactionsAsync());
            Assert.NotNull(await _repository.GetTransactionById(reversal.Id));
        }

        // ---- ReverseTransaction: bank balance and statement line (AC 4, AC 7) ----

        // Book balance 50 000, reversing a receipt of 5 000: 45 000.
        [Fact]
        public async Task ReverseTransaction_OutgoingReversal_LowersBookBalance()
        {
            var s = await _seed.SeedArReceiptAsync();

            var reversal = await _repository.ReverseTransaction(Posting(s, "Receipt", "out", 5000m, ArRestored(s)));

            Assert.Equal(45000m, reversal.Book_Balance);
            Assert.Equal(45000m, await _seed.GetBookBalanceAsync());
        }

        [Fact]
        public async Task ReverseTransaction_IncomingReversal_RaisesBookBalance()
        {
            var s = await _seed.SeedApPaymentAsync();

            var reversal = await _repository.ReverseTransaction(Posting(s, "Payment", "in", 3000m,
                Document("APBill", s.BillId!.Value, 8000m, 0m, 8000m, "Unpaid")));

            Assert.Equal(53000m, reversal.Book_Balance);
            Assert.Equal(53000m, await _seed.GetBookBalanceAsync());
        }

        [Fact]
        public async Task ReverseTransaction_OutgoingReversalOfExactlyTheBalance_IsAllowedAndLeavesZero()
        {
            var s = await _seed.SeedMiscReceiptAsync(1000m);
            await _seed.SetBookBalanceAsync(1000m);

            var reversal = await _repository.ReverseTransaction(Posting(s, "Receipt", "out", 1000m));

            Assert.Equal(0m, reversal.Book_Balance);
        }

        [Fact]
        public async Task ReverseTransaction_OutgoingReversal_GetsItsOwnWithdrawalStatementLine()
        {
            var s = await _seed.SeedArReceiptAsync();

            var reversal = await _repository.ReverseTransaction(Posting(s, "Receipt", "out", 5000m, ArRestored(s)));

            Assert.True(reversal.Statement_Line_Id > 0);
            using var c = _seed.Open();
            var line = await c.QuerySingleAsync<LineRow>(
                @"SELECT id, transaction_id, bank_account_id, type, direction::text AS direction, deposit, withdrawal, running_balance,
                         statement_status, match_status, source, posted_date::timestamp AS posted_date, party
                  FROM bank_statement_line WHERE transaction_id = @Id", new { Id = reversal.Id });
            Assert.Equal(reversal.Statement_Line_Id, line.Id);
            Assert.Equal(_seed.AccountId, line.Bank_Account_Id);
            Assert.Equal("Reversal", line.Type);
            Assert.Equal("out", line.Direction);
            Assert.Equal(0m, line.Deposit);
            Assert.Equal(5000m, line.Withdrawal);
            // The original line's running balance was 50 000.
            Assert.Equal(45000m, line.Running_Balance);
            Assert.Equal("Cleared", line.Statement_Status);
            Assert.Equal("Matched", line.Match_Status);
            Assert.Equal("Simulated", line.Source);
            Assert.Equal(DateTime.UtcNow.Date, line.Posted_Date);
            Assert.Equal("Acme", line.Party);
        }

        [Fact]
        public async Task ReverseTransaction_IncomingReversal_GetsItsOwnDepositStatementLine()
        {
            var s = await _seed.SeedApPaymentAsync();

            var reversal = await _repository.ReverseTransaction(Posting(s, "Payment", "in", 3000m,
                Document("APBill", s.BillId!.Value, 8000m, 0m, 8000m, "Unpaid")));

            using var c = _seed.Open();
            var line = await c.QuerySingleAsync<LineRow>(
                @"SELECT id, deposit, withdrawal, running_balance, direction::text AS direction
                  FROM bank_statement_line WHERE transaction_id = @Id", new { Id = reversal.Id });
            Assert.Equal("in", line.Direction);
            Assert.Equal(3000m, line.Deposit);
            Assert.Equal(0m, line.Withdrawal);
            Assert.Equal(53000m, line.Running_Balance);
        }

        [Fact]
        public async Task ReverseTransaction_KeepsTheOriginalStatementLineAndAddsOne()
        {
            var s = await _seed.SeedArReceiptAsync();

            await _repository.ReverseTransaction(Posting(s, "Receipt", "out", 5000m, ArRestored(s)));

            Assert.Equal(2, await _seed.CountStatementLinesAsync());
            Assert.Equal(1, await ScalarAsync<int>(
                "SELECT COUNT(*) FROM bank_statement_line WHERE transaction_id = @Id", new { Id = s.TransactionId }));
        }

        [Fact]
        public async Task ReverseTransaction_AccountWithoutStatementLines_StartsRunningBalanceFromOpeningBalance()
        {
            var s = await _seed.SeedMiscReceiptAsync(1000m);
            using (var c = _seed.Open())
                await c.ExecuteAsync("DELETE FROM bank_statement_line WHERE bank_account_id = @Id", new { Id = _seed.AccountId });
            await ScalarAsync<int>("UPDATE bank_account SET opening_balance = 200 WHERE id = @Id RETURNING 1", new { Id = _seed.AccountId });

            var reversal = await _repository.ReverseTransaction(Posting(s, "Receipt", "out", 1000m));

            Assert.Equal(-800m, await ScalarAsync<decimal>(
                "SELECT running_balance FROM bank_statement_line WHERE id = @Id", new { Id = reversal.Statement_Line_Id }));
        }

        // ---- ReverseTransaction: original receipt / payment ----

        [Fact]
        public async Task ReverseTransaction_Receipt_MarksTheOriginalReceiptReversedAndKeepsTheRow()
        {
            var s = await _seed.SeedArReceiptAsync();

            await _repository.ReverseTransaction(Posting(s, "Receipt", "out", 5000m, ArRestored(s)));

            Assert.Equal("Reversed", await ScalarAsync<string>("SELECT status FROM bank_receipt WHERE id = @Id", new { Id = s.SourceId }));
            Assert.Equal(5000m, await ScalarAsync<decimal>("SELECT amount FROM bank_receipt WHERE id = @Id", new { Id = s.SourceId }));
        }

        [Fact]
        public async Task ReverseTransaction_Payment_MarksTheOriginalPaymentReversedAndKeepsTheRow()
        {
            var s = await _seed.SeedApPaymentAsync();

            await _repository.ReverseTransaction(Posting(s, "Payment", "in", 3000m,
                Document("APBill", s.BillId!.Value, 8000m, 0m, 8000m, "Unpaid")));

            Assert.Equal("Reversed", await ScalarAsync<string>("SELECT status FROM bank_payment WHERE id = @Id", new { Id = s.SourceId }));
        }

        // ---- ReverseTransaction: documents and party balances (AC 4) ----

        [Fact]
        public async Task ReverseTransaction_ArReceipt_RestoresArInvoiceAndSalesInvoice()
        {
            var s = await _seed.SeedArReceiptAsync();

            await _repository.ReverseTransaction(Posting(s, "Receipt", "out", 5000m, ArRestored(s)));

            using var c = _seed.Open();
            var ar = await c.QuerySingleAsync<DocRow>("SELECT paid, balance, status FROM ar_invoice WHERE id = @Id", new { Id = s.ArId });
            var invoice = await c.QuerySingleAsync<DocRow>("SELECT paid, balance, payment_status AS status FROM sales_invoice WHERE id = @Id", new { Id = s.InvoiceId });
            Assert.Equal((10000m, 10000m, "partial"), (ar.Paid, ar.Balance, ar.Status));
            Assert.Equal((10000m, 10000m, "partial"), (invoice.Paid, invoice.Balance, invoice.Status));
        }

        [Fact]
        public async Task ReverseTransaction_ArReceiptThatWasTheOnlyPayment_ReturnsDocumentsToUnpaid()
        {
            var s = await _seed.SeedArReceiptAsync(receiptAmount: 5000m, invoiceTotal: 5000m, paidAfter: 5000m);

            await _repository.ReverseTransaction(Posting(s, "Receipt", "out", 5000m, new[]
            {
                Document("ARInvoice", s.ArId!.Value, 5000m, 0m, 5000m, "Unpaid"),
                Document("SalesInvoice", s.InvoiceId!.Value, 5000m, 0m, 5000m, "Unpaid")
            }));

            using var c = _seed.Open();
            var ar = await c.QuerySingleAsync<DocRow>("SELECT paid, balance, status FROM ar_invoice WHERE id = @Id", new { Id = s.ArId });
            var invoice = await c.QuerySingleAsync<DocRow>("SELECT paid, balance, payment_status AS status FROM sales_invoice WHERE id = @Id", new { Id = s.InvoiceId });
            Assert.Equal((0m, 5000m, "unpaid"), (ar.Paid, ar.Balance, ar.Status));
            Assert.Equal((0m, 5000m, "unpaid"), (invoice.Paid, invoice.Balance, invoice.Status));
        }

        // Customer.Balance = sum of the customer's open AR balances: 10 000 after the reversal (was 5 000).
        [Fact]
        public async Task ReverseTransaction_ArReceipt_RecalculatesCustomerBalanceFromOpenAr()
        {
            var s = await _seed.SeedArReceiptAsync();

            await _repository.ReverseTransaction(Posting(s, "Receipt", "out", 5000m, ArRestored(s)));

            Assert.Equal(10000m, await ScalarAsync<decimal>("SELECT balance FROM customer WHERE id = @Id", new { Id = _seed.CustomerId }));
        }

        [Fact]
        public async Task ReverseTransaction_CustomerBalanceCountsAllOpenArOfTheCustomer()
        {
            var first = await _seed.SeedArReceiptAsync();
            var second = await _seed.SeedArReceiptAsync(receiptAmount: 1000m, invoiceTotal: 4000m, paidAfter: 1000m);

            await _repository.ReverseTransaction(Posting(first, "Receipt", "out", 5000m, ArRestored(first)));

            // AR 1 now has 10 000 open, AR 2 still has 3 000 open.
            Assert.Equal(13000m, await ScalarAsync<decimal>("SELECT balance FROM customer WHERE id = @Id", new { Id = _seed.CustomerId }));
            Assert.NotEqual(first.ArId, second.ArId);
        }

        [Fact]
        public async Task ReverseTransaction_ApPayment_RestoresBillAndRecalculatesVendorBalance()
        {
            var s = await _seed.SeedApPaymentAsync();

            await _repository.ReverseTransaction(Posting(s, "Payment", "in", 3000m,
                Document("APBill", s.BillId!.Value, 8000m, 0m, 8000m, "Unpaid")));

            using var c = _seed.Open();
            var bill = await c.QuerySingleAsync<DocRow>("SELECT paid, balance, status FROM ap_bill WHERE id = @Id", new { Id = s.BillId });
            Assert.Equal((0m, 8000m, "unpaid"), (bill.Paid, bill.Balance, bill.Status));
            Assert.Equal(8000m, await ScalarAsync<decimal>("SELECT balance FROM vendor WHERE id = @Id", new { Id = _seed.VendorId }));
        }

        [Fact]
        public async Task ReverseTransaction_MiscReceipt_ChangesNoDocumentOrCustomer()
        {
            var ar = await _seed.SeedArReceiptAsync();
            var misc = await _seed.SeedMiscReceiptAsync(1000m);

            await _repository.ReverseTransaction(Posting(misc, "Receipt", "out", 1000m));

            Assert.Equal(15000m, await ScalarAsync<decimal>("SELECT paid FROM ar_invoice WHERE id = @Id", new { Id = ar.ArId }));
            Assert.Equal(5000m, await ScalarAsync<decimal>("SELECT balance FROM customer WHERE id = @Id", new { Id = _seed.CustomerId }));
        }

        // ---- ReverseTransaction: rules and rollback ----

        [Fact]
        public async Task ReverseTransaction_SameTransactionTwice_SecondThrowsAndChangesNothing()
        {
            var s = await _seed.SeedArReceiptAsync();
            await _repository.ReverseTransaction(Posting(s, "Receipt", "out", 5000m, ArRestored(s)));

            var ex = await Assert.ThrowsAsync<ValidationException>(() =>
                _repository.ReverseTransaction(Posting(s, "Receipt", "out", 5000m, ArRestored(s, paid: 5000m, balance: 15000m))));

            Assert.Contains("already been reversed", ex.Message);
            Assert.Equal(2, await _seed.CountTransactionsAsync());
            Assert.Equal(2, await _seed.CountStatementLinesAsync());
            Assert.Equal(45000m, await _seed.GetBookBalanceAsync());
            Assert.Equal(10000m, await ScalarAsync<decimal>("SELECT paid FROM ar_invoice WHERE id = @Id", new { Id = s.ArId }));
        }

        [Fact]
        public async Task ReverseTransaction_OutgoingAmountAboveBookBalance_ThrowsAndChangesNothing()
        {
            var s = await _seed.SeedArReceiptAsync();
            await _seed.SetBookBalanceAsync(4999.99m);

            var ex = await Assert.ThrowsAsync<ValidationException>(() =>
                _repository.ReverseTransaction(Posting(s, "Receipt", "out", 5000m, ArRestored(s))));

            Assert.Contains("book balance", ex.Message);
            Assert.Equal(1, await _seed.CountTransactionsAsync());
            Assert.Equal(1, await _seed.CountStatementLinesAsync());
            Assert.Equal(4999.99m, await _seed.GetBookBalanceAsync());
            Assert.Equal("Posted", await ScalarAsync<string>("SELECT status FROM bank_receipt WHERE id = @Id", new { Id = s.SourceId }));
            Assert.Equal(15000m, await ScalarAsync<decimal>("SELECT paid FROM ar_invoice WHERE id = @Id", new { Id = s.ArId }));
        }

        [Fact]
        public async Task ReverseTransaction_IncomingReversal_IsNotLimitedByBookBalance()
        {
            var s = await _seed.SeedApPaymentAsync();
            await _seed.SetBookBalanceAsync(0m);

            var reversal = await _repository.ReverseTransaction(Posting(s, "Payment", "in", 3000m,
                Document("APBill", s.BillId!.Value, 8000m, 0m, 8000m, "Unpaid")));

            Assert.Equal(3000m, reversal.Book_Balance);
        }

        [Fact]
        public async Task ReverseTransaction_InactiveAccount_ThrowsAndChangesNothing()
        {
            var s = await _seed.SeedMiscReceiptAsync();
            await ScalarAsync<int>("UPDATE bank_account SET status = 'inactive' WHERE id = @Id RETURNING 1", new { Id = _seed.AccountId });

            var ex = await Assert.ThrowsAsync<ValidationException>(() => _repository.ReverseTransaction(Posting(s, "Receipt", "out", 1000m)));

            Assert.Contains("active", ex.Message);
            Assert.Equal(1, await _seed.CountTransactionsAsync());
        }

        [Fact]
        public async Task ReverseTransaction_UnknownAccount_Throws()
        {
            var s = await _seed.SeedMiscReceiptAsync();
            var posting = Posting(s, "Receipt", "out", 1000m);
            posting.Bank_Account_Id = long.MaxValue;

            await Assert.ThrowsAsync<ValidationException>(() => _repository.ReverseTransaction(posting));

            Assert.Equal(1, await _seed.CountTransactionsAsync());
        }

        // A document changed by another payment in the meantime: nothing at all is saved.
        [Fact]
        public async Task ReverseTransaction_DocumentPaidChangedMeanwhile_ThrowsAndRollsBackEverything()
        {
            var s = await _seed.SeedArReceiptAsync();
            await ScalarAsync<int>("UPDATE ar_invoice SET paid = 14000, balance = 6000 WHERE id = @Id RETURNING 1", new { Id = s.ArId });

            var ex = await Assert.ThrowsAsync<ValidationException>(() =>
                _repository.ReverseTransaction(Posting(s, "Receipt", "out", 5000m, ArRestored(s))));

            Assert.Contains("changed", ex.Message);
            Assert.Equal(1, await _seed.CountTransactionsAsync());
            Assert.Equal(1, await _seed.CountStatementLinesAsync());
            Assert.Equal(50000m, await _seed.GetBookBalanceAsync());
            Assert.Equal("Posted", await ScalarAsync<string>("SELECT status FROM bank_receipt WHERE id = @Id", new { Id = s.SourceId }));
            Assert.Equal(15000m, await ScalarAsync<decimal>("SELECT paid FROM sales_invoice WHERE id = @Id", new { Id = s.InvoiceId }));
            Assert.Equal(5000m, await ScalarAsync<decimal>("SELECT balance FROM customer WHERE id = @Id", new { Id = _seed.CustomerId }));
        }

        [Fact]
        public async Task ReverseTransaction_UnknownDocumentType_ThrowsAndRollsBackEverything()
        {
            var s = await _seed.SeedMiscReceiptAsync();

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _repository.ReverseTransaction(Posting(s, "Receipt", "out", 1000m, Document("Voucher", 1, 10m, 0m, 10m, "Unpaid"))));

            Assert.Equal(1, await _seed.CountTransactionsAsync());
            Assert.Equal(50000m, await _seed.GetBookBalanceAsync());
        }

        [Fact]
        public async Task ReverseTransaction_SourceRowMissing_ThrowsAndRollsBackEverything()
        {
            var s = await _seed.SeedMiscReceiptAsync();
            var posting = Posting(s, "Receipt", "out", 1000m);
            posting.Source_Id = long.MaxValue;

            await Assert.ThrowsAsync<ValidationException>(() => _repository.ReverseTransaction(posting));

            Assert.Equal(1, await _seed.CountTransactionsAsync());
            Assert.Equal(50000m, await _seed.GetBookBalanceAsync());
        }

        private class LineRow
        {
            public long Id { get; set; }
            public long Transaction_Id { get; set; }
            public long Bank_Account_Id { get; set; }
            public string Type { get; set; } = "";
            public string Direction { get; set; } = "";
            public decimal Deposit { get; set; }
            public decimal Withdrawal { get; set; }
            public decimal Running_Balance { get; set; }
            public string Statement_Status { get; set; } = "";
            public string Match_Status { get; set; } = "";
            public string Source { get; set; } = "";
            public DateTime Posted_Date { get; set; }
            public string? Party { get; set; }
        }

        private class DocRow
        {
            public decimal Paid { get; set; }
            public decimal Balance { get; set; }
            public string Status { get; set; } = "";
        }
    }
}
