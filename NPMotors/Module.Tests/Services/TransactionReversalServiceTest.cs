using System.ComponentModel.DataAnnotations;
using System.Data;
using NPMotors.Repository.Interface;
using NPMotors.Repository.Models;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.RequestFeatures;
using NPMotors.Services.Service;

namespace Module.Tests.Services
{
    // Story 23: TransactionService.ReverseTransaction. EnsureNotReversed, GetReverseDirection and
    // RestoreSourceBalances are private, so they are tested only through this public method.
    public class TransactionReversalServiceTest
    {
        // Hand-written fake: the test projects do not use a mocking library.
        private class FakeTransactionRepository : ITransactionRepository
        {
            public ReversalSourceDto? Source { get; set; }
            public TransactionResponseDto PostResult { get; set; } = new() { Id = 900, Type = "Reversal" };

            public List<long> SourceCalls { get; } = new();
            public List<ReversalPostingDto> PostCalls { get; } = new();

            public Task<ReversalSourceDto?> GetTransactionForReversal(long id)
            {
                SourceCalls.Add(id);
                return Task.FromResult(Source);
            }

            public Task<TransactionResponseDto> ReverseTransaction(ReversalPostingDto posting)
            {
                PostCalls.Add(posting);
                return Task.FromResult(PostResult);
            }

            public Task<PagedList<TransactionResponseDto>> GetAllTransactions(TransactionParameters parameters) =>
                throw new NotSupportedException();

            public Task<TransactionResponseDto?> GetTransactionById(long id) => throw new NotSupportedException();
            public Task<long> GetNextTransactionId(IDbTransaction tx) => throw new NotSupportedException();
            public Task<long> InsertTransaction(Transaction transaction, IDbTransaction tx) => throw new NotSupportedException();
        }

        private readonly FakeTransactionRepository _repository = new();
        private readonly TransactionService _service;

        public TransactionReversalServiceTest()
        {
            _service = new TransactionService(_repository);
        }

        private static ReverseTransactionDto Reversal(string reason = "Wrong amount") => new() { Reason = reason };

        private static SourceBalanceDto Document(string type, long id, decimal amount, decimal paid, decimal balance, string status) => new()
        {
            Document_Type = type, Document_Id = id, Amount = amount, Paid = paid, Balance = balance, Status = status
        };

        // A posted receipt of 5 000 on an AR invoice of 20 000 that shows Paid 15 000 / Balance 5 000.
        private static ReversalSourceDto ReceiptSource(params SourceBalanceDto[] documents) => new()
        {
            Transaction_Id = 7,
            Type = "BankReceipt",
            Direction = "in",
            Amount = 5000m,
            Status = "posted",
            Bank_Account_Id = 3,
            Source_Type = "Receipt",
            Source_Id = 11,
            Source_Number = "RCT-2026-000011",
            Source_Status = "Posted",
            Linked_Documents = documents.Length > 0
                ? documents.ToList()
                : new List<SourceBalanceDto> { Document("ARInvoice", 1, 20000m, 15000m, 5000m, "partial") }
        };

        private static ReversalSourceDto PaymentSource(decimal amount, params SourceBalanceDto[] documents) => new()
        {
            Transaction_Id = 8,
            Type = "BankPayment",
            Direction = "out",
            Amount = amount,
            Status = "posted",
            Bank_Account_Id = 3,
            Source_Type = "Payment",
            Source_Id = 21,
            Source_Number = "PAY-2026-000021",
            Source_Status = "Posted",
            Linked_Documents = documents.ToList()
        };

        private async Task<ReversalPostingDto> ReverseAsync(ReversalSourceDto source, string reason = "Wrong amount")
        {
            _repository.Source = source;
            await _service.ReverseTransaction(source.Transaction_Id, Reversal(reason));
            return Assert.Single(_repository.PostCalls);
        }

        // ---- ReverseTransaction: orchestration ----

        [Fact]
        public async Task ReverseTransaction_Valid_ReturnsTheRepositoryReversal()
        {
            _repository.Source = ReceiptSource();

            var result = await _service.ReverseTransaction(7, Reversal());

            Assert.Same(_repository.PostResult, result);
        }

        [Fact]
        public async Task ReverseTransaction_PassesIdToRepository()
        {
            _repository.Source = ReceiptSource();

            await _service.ReverseTransaction(7, Reversal());

            Assert.Equal(7, Assert.Single(_repository.SourceCalls));
        }

        [Fact]
        public async Task ReverseTransaction_NotFound_ThrowsKeyNotFoundAndPostsNothing()
        {
            _repository.Source = null;

            await Assert.ThrowsAsync<KeyNotFoundException>(() => _service.ReverseTransaction(99, Reversal()));

            Assert.Empty(_repository.PostCalls);
        }

        // AC 1: the posting references the original transaction and carries the reason.
        [Fact]
        public async Task ReverseTransaction_PostingReferencesOriginalTransactionAndReason()
        {
            var posting = await ReverseAsync(ReceiptSource(), "Entered twice");

            Assert.Equal(7, posting.Original_Transaction_Id);
            Assert.Equal("Entered twice", posting.Reason);
        }

        [Fact]
        public async Task ReverseTransaction_ReasonIsTrimmed()
        {
            var posting = await ReverseAsync(ReceiptSource(), "   Entered twice  ");

            Assert.Equal("Entered twice", posting.Reason);
        }

        [Fact]
        public async Task ReverseTransaction_ReasonOfExactly500Characters_IsAccepted()
        {
            var posting = await ReverseAsync(ReceiptSource(), new string('r', 500));

            Assert.Equal(500, posting.Reason.Length);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public async Task ReverseTransaction_MissingReason_ThrowsAndTouchesNothing(string? reason)
        {
            _repository.Source = ReceiptSource();

            await Assert.ThrowsAsync<ValidationException>(() =>
                _service.ReverseTransaction(7, new ReverseTransactionDto { Reason = reason! }));

            Assert.Empty(_repository.SourceCalls);
            Assert.Empty(_repository.PostCalls);
        }

        [Fact]
        public async Task ReverseTransaction_NullBody_ThrowsValidationException()
        {
            _repository.Source = ReceiptSource();

            await Assert.ThrowsAsync<ValidationException>(() => _service.ReverseTransaction(7, null!));

            Assert.Empty(_repository.PostCalls);
        }

        [Fact]
        public async Task ReverseTransaction_ReasonOf501Characters_ThrowsAndTouchesNothing()
        {
            _repository.Source = ReceiptSource();

            await Assert.ThrowsAsync<ValidationException>(() => _service.ReverseTransaction(7, Reversal(new string('r', 501))));

            Assert.Empty(_repository.SourceCalls);
            Assert.Empty(_repository.PostCalls);
        }

        // AC 3: the reversal amount equals the amount being reversed.
        [Fact]
        public async Task ReverseTransaction_PostingAmountEqualsSourceAmount()
        {
            var source = ReceiptSource();
            source.Amount = 1234.56m;

            var posting = await ReverseAsync(source);

            Assert.Equal(1234.56m, posting.Amount);
        }

        [Fact]
        public async Task ReverseTransaction_PostingCarriesAccountAndSourceDocument()
        {
            var posting = await ReverseAsync(ReceiptSource());

            Assert.Equal(3, posting.Bank_Account_Id);
            Assert.Equal("Receipt", posting.Source_Type);
            Assert.Equal(11, posting.Source_Id);
            Assert.Equal("RCT-2026-000011", posting.Source_Number);
        }

        // ---- -EnsureNotReversed ----

        [Theory]
        [InlineData("Receipt")]
        [InlineData("Payment")]
        public async Task ReverseTransaction_PostedReceiptOrPayment_IsAllowed(string sourceType)
        {
            var source = ReceiptSource();
            source.Source_Type = sourceType;

            await ReverseAsync(source);
        }

        [Fact]
        public async Task ReverseTransaction_StatusPostedInAnyCasing_IsAllowed()
        {
            var source = ReceiptSource();
            source.Status = "Posted";

            await ReverseAsync(source);
        }

        [Fact]
        public async Task ReverseTransaction_DraftTransaction_ThrowsAndPostsNothing()
        {
            var source = ReceiptSource();
            source.Status = "draft";
            _repository.Source = source;

            await Assert.ThrowsAsync<ValidationException>(() => _service.ReverseTransaction(7, Reversal()));

            Assert.Empty(_repository.PostCalls);
        }

        [Theory]
        [InlineData("")]
        [InlineData("Transfer")]
        public async Task ReverseTransaction_NotAReceiptOrPayment_ThrowsAndPostsNothing(string sourceType)
        {
            var source = ReceiptSource();
            source.Source_Type = sourceType;
            _repository.Source = source;

            await Assert.ThrowsAsync<ValidationException>(() => _service.ReverseTransaction(7, Reversal()));

            Assert.Empty(_repository.PostCalls);
        }

        // A reversal is never reversed itself.
        [Fact]
        public async Task ReverseTransaction_SourceIsItselfAReversal_ThrowsAndPostsNothing()
        {
            var source = ReceiptSource();
            source.Reverses_Transaction_Id = 3;
            _repository.Source = source;

            await Assert.ThrowsAsync<ValidationException>(() => _service.ReverseTransaction(7, Reversal()));

            Assert.Empty(_repository.PostCalls);
        }

        // AC 6: the same transaction cannot be reversed twice.
        [Fact]
        public async Task ReverseTransaction_AlreadyReversed_ThrowsAndPostsNothing()
        {
            var source = ReceiptSource();
            source.Reversed_By_Transaction_Id = 55;
            _repository.Source = source;

            var ex = await Assert.ThrowsAsync<ValidationException>(() => _service.ReverseTransaction(7, Reversal()));

            Assert.Contains("already been reversed", ex.Message);
            Assert.Empty(_repository.PostCalls);
        }

        // ---- -GetReverseDirection (AC 2) ----

        [Fact]
        public async Task ReverseTransaction_ReceiptIn_PostsAnOutgoingReversal()
        {
            var posting = await ReverseAsync(ReceiptSource());

            Assert.Equal("out", posting.Direction);
        }

        [Fact]
        public async Task ReverseTransaction_PaymentOut_PostsAnIncomingReversal()
        {
            var posting = await ReverseAsync(PaymentSource(3000m));

            Assert.Equal("in", posting.Direction);
        }

        [Fact]
        public async Task ReverseTransaction_DirectionInUpperCase_StillReversesToOut()
        {
            var source = ReceiptSource();
            source.Direction = "IN";

            var posting = await ReverseAsync(source);

            Assert.Equal("out", posting.Direction);
        }

        [Fact]
        public async Task ReverseTransaction_UnknownDirection_ThrowsAndPostsNothing()
        {
            var source = ReceiptSource();
            source.Direction = "sideways";
            _repository.Source = source;

            await Assert.ThrowsAsync<ValidationException>(() => _service.ReverseTransaction(7, Reversal()));

            Assert.Empty(_repository.PostCalls);
        }

        // ---- -RestoreSourceBalances (AC 4) ----

        // Diagram example: receipt 5 000 on AR 20 000 (Paid 15 000) -> Paid 10 000, Balance 10 000, "Partial".
        [Fact]
        public async Task ReverseTransaction_PartiallyPaidDocument_RestoresPaidBalanceAndPartialStatus()
        {
            var posting = await ReverseAsync(ReceiptSource());

            var restored = Assert.Single(posting.Restored_Documents);
            Assert.Equal("ARInvoice", restored.Document_Type);
            Assert.Equal(1, restored.Document_Id);
            Assert.Equal(20000m, restored.Amount);
            Assert.Equal(10000m, restored.Paid);
            Assert.Equal(10000m, restored.Balance);
            Assert.Equal("Partial", restored.Status);
        }

        [Fact]
        public async Task ReverseTransaction_ReceiptWasTheOnlyPayment_DocumentBecomesUnpaid()
        {
            var posting = await ReverseAsync(ReceiptSource(Document("ARInvoice", 1, 5000m, 5000m, 0m, "paid")));

            var restored = Assert.Single(posting.Restored_Documents);
            Assert.Equal(0m, restored.Paid);
            Assert.Equal(5000m, restored.Balance);
            Assert.Equal("Unpaid", restored.Status);
        }

        [Fact]
        public async Task ReverseTransaction_ArInvoiceAndSalesInvoice_BothAreRestoredInOrder()
        {
            var posting = await ReverseAsync(ReceiptSource(
                Document("ARInvoice", 1, 20000m, 15000m, 5000m, "partial"),
                Document("SalesInvoice", 2, 20000m, 15000m, 5000m, "partial")));

            Assert.Equal(new[] { "ARInvoice", "SalesInvoice" }, posting.Restored_Documents.Select(d => d.Document_Type).ToArray());
            Assert.Equal(new long[] { 1, 2 }, posting.Restored_Documents.Select(d => d.Document_Id).ToArray());
            Assert.All(posting.Restored_Documents, d =>
            {
                Assert.Equal(10000m, d.Paid);
                Assert.Equal(10000m, d.Balance);
                Assert.Equal("Partial", d.Status);
            });
        }

        // A bill of 8 000 with 3 000 paid, the payment of 3 000 reversed: Paid 0, Balance 8 000, "Unpaid".
        [Fact]
        public async Task ReverseTransaction_PaymentOnApBill_RestoresBill()
        {
            var posting = await ReverseAsync(PaymentSource(3000m, Document("APBill", 4, 8000m, 3000m, 5000m, "partial")));

            var restored = Assert.Single(posting.Restored_Documents);
            Assert.Equal("APBill", restored.Document_Type);
            Assert.Equal(0m, restored.Paid);
            Assert.Equal(8000m, restored.Balance);
            Assert.Equal("Unpaid", restored.Status);
        }

        // Bill with 3 000 paid, a 1 000 payment reversed: Paid 2 000, Balance 6 000, "Partial".
        [Fact]
        public async Task ReverseTransaction_OneOfSeveralPaymentsReversed_BillStaysPartial()
        {
            var posting = await ReverseAsync(PaymentSource(1000m, Document("APBill", 4, 8000m, 3000m, 5000m, "partial")));

            var restored = Assert.Single(posting.Restored_Documents);
            Assert.Equal(2000m, restored.Paid);
            Assert.Equal(6000m, restored.Balance);
            Assert.Equal("Partial", restored.Status);
        }

        [Fact]
        public async Task ReverseTransaction_DecimalAmounts_AreRestoredExactly()
        {
            var source = ReceiptSource(Document("ARInvoice", 1, 5000m, 2000m, 3000m, "partial"));
            source.Amount = 1234.56m;

            var posting = await ReverseAsync(source);

            var restored = Assert.Single(posting.Restored_Documents);
            Assert.Equal(765.44m, restored.Paid);
            Assert.Equal(4234.56m, restored.Balance);
            Assert.Equal("Partial", restored.Status);
        }

        // The formula uses the reversed amount and the document's own Paid/Balance, not the document's Amount.
        [Fact]
        public async Task ReverseTransaction_RestoredValuesDoNotDependOnDocumentAmount()
        {
            var posting = await ReverseAsync(ReceiptSource(Document("ARInvoice", 1, 99999m, 15000m, 5000m, "partial")));

            var restored = Assert.Single(posting.Restored_Documents);
            Assert.Equal(10000m, restored.Paid);
            Assert.Equal(10000m, restored.Balance);
            Assert.Equal(99999m, restored.Amount);
        }

        // A vehicle paid in full has no AP bill, and a misc receipt has no invoice: nothing to restore.
        [Fact]
        public async Task ReverseTransaction_NoLinkedDocuments_RestoresNothingButStillReverses()
        {
            var source = ReceiptSource();
            source.Linked_Documents = new List<SourceBalanceDto>();

            var posting = await ReverseAsync(source);

            Assert.Empty(posting.Restored_Documents);
            Assert.Equal("out", posting.Direction);
        }

        [Fact]
        public async Task ReverseTransaction_DoesNotChangeTheSourceDocuments()
        {
            var source = ReceiptSource();

            await ReverseAsync(source);

            var document = Assert.Single(source.Linked_Documents);
            Assert.Equal(15000m, document.Paid);
            Assert.Equal(5000m, document.Balance);
        }
    }
}
