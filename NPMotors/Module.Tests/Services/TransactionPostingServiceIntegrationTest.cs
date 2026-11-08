using System.ComponentModel.DataAnnotations;
using Dapper;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.Models.Enums;
using NPMotors.Repository.Repositories;
using NPMotors.Services.Service;

namespace Module.Tests.Services
{
    // Posting through the real repositories and the test database (AC 3, 4, 5 end to end).
    public class TransactionPostingServiceIntegrationTest : TestBase, IAsyncLifetime
    {
        private readonly string _marker = $"itest-{Guid.NewGuid():N}".Substring(0, 18);
        private long _bankId;
        private long _accountId;
        private TransactionPostingService _service = null!;
        private TransactionRepository _transactions = null!;

        public async Task InitializeAsync()
        {
            _transactions = new TransactionRepository(_repositoryContext);
            _service = new TransactionPostingService(
                _transactions, new BankAccountRepository(_repositoryContext), _repositoryContext);

            using var connection = _repositoryContext.GetDatabaseConnection();
            _bankId = await connection.ExecuteScalarAsync<long>(
                "INSERT INTO bank (name) VALUES (@Name) RETURNING id",
                new { Name = $"{_marker} Bank" });
            _accountId = await connection.ExecuteScalarAsync<long>(
                @"INSERT INTO bank_account (bank_id, name, type, opening_balance, book_balance, status)
                  VALUES (@BankId, @Name, 'Checking', 100, 100, 'active') RETURNING id",
                new { BankId = _bankId, Name = $"{_marker} Account" });
        }

        public async Task DisposeAsync()
        {
            using var connection = _repositoryContext.GetDatabaseConnection();
            await connection.ExecuteAsync(
                @"DELETE FROM ""transaction"" WHERE bank_account_id IN (SELECT id FROM bank_account WHERE bank_id = @Id)",
                new { Id = _bankId });
            await connection.ExecuteAsync("DELETE FROM bank_account WHERE bank_id = @Id", new { Id = _bankId });
            await connection.ExecuteAsync("DELETE FROM bank WHERE id = @Id", new { Id = _bankId });
        }

        private PostTransactionRequest Request(Direction direction, decimal amount, TransactionStatus status) => new()
        {
            BankAccountId = _accountId,
            Direction = direction,
            Amount = amount,
            Date = new DateTime(2026, 3, 15),
            Type = TransactionType.BankAdjustment,
            SourceNumber = "ADJ-1",
            Status = status
        };

        private async Task<decimal> BookBalanceAsync()
        {
            using var connection = _repositoryContext.GetDatabaseConnection();
            return await connection.ExecuteScalarAsync<decimal>(
                "SELECT book_balance FROM bank_account WHERE id = @Id", new { Id = _accountId });
        }

        private async Task<int> TransactionCountAsync()
        {
            using var connection = _repositoryContext.GetDatabaseConnection();
            return await connection.ExecuteScalarAsync<int>(
                @"SELECT COUNT(*) FROM ""transaction"" WHERE bank_account_id = @Id", new { Id = _accountId });
        }

        [Fact]
        public async Task PostTransaction_PostedIncoming_SavesRowAndIncreasesBalance()
        {
            var id = await _service.PostTransaction(Request(Direction.In, 40m, TransactionStatus.Posted));

            var saved = await _transactions.GetTransactionById(id);
            Assert.Equal("posted", saved!.Status);
            Assert.Equal("in", saved.Direction);
            Assert.Equal("BankAdjustment", saved.Type);
            Assert.Equal("ADJ-1", saved.Source_Number);
            Assert.Equal($"TXN-{DateTime.UtcNow.Year}-{id:D6}", saved.Number);
            Assert.Equal(140m, await BookBalanceAsync());
        }

        [Fact]
        public async Task PostTransaction_PostedOutgoing_DecreasesBalance()
        {
            await _service.PostTransaction(Request(Direction.Out, 30m, TransactionStatus.Posted));

            Assert.Equal(70m, await BookBalanceAsync());
        }

        [Fact]
        public async Task PostTransaction_OutgoingAboveBalance_ThrowsAndLeavesNoRowAndBalanceUnchanged()
        {
            await Assert.ThrowsAsync<ValidationException>(
                () => _service.PostTransaction(Request(Direction.Out, 100.01m, TransactionStatus.Posted)));

            Assert.Equal(0, await TransactionCountAsync());
            Assert.Equal(100m, await BookBalanceAsync());
        }

        [Fact]
        public async Task PostTransaction_Draft_SavesRowButBalanceUnchanged()
        {
            var id = await _service.PostTransaction(Request(Direction.Out, 500m, TransactionStatus.Draft));

            Assert.Equal("draft", (await _transactions.GetTransactionById(id))!.Status);
            Assert.Equal(100m, await BookBalanceAsync());
        }

        [Fact]
        public async Task PostTransaction_CallersTxRolledBack_UndoesRowAndBalance()
        {
            using var connection = _repositoryContext.GetDatabaseConnection();
            using (var tx = connection.BeginTransaction())
            {
                await _service.PostTransaction(Request(Direction.In, 40m, TransactionStatus.Posted), tx);
                tx.Rollback();
            }

            Assert.Equal(0, await TransactionCountAsync());
            Assert.Equal(100m, await BookBalanceAsync());
        }

        [Fact]
        public async Task PostTransaction_TwoOutgoingPostingsTogetherExceedingBalance_OnlyOneSucceeds()
        {
            // 70 + 70 > 100: the row lock makes the second posting see the reduced balance.
            var results = await Task.WhenAll(
                Attempt(Request(Direction.Out, 70m, TransactionStatus.Posted)),
                Attempt(Request(Direction.Out, 70m, TransactionStatus.Posted)));

            Assert.Equal(1, results.Count(r => r));
            Assert.Equal(30m, await BookBalanceAsync());
            Assert.Equal(1, await TransactionCountAsync());
        }

        private async Task<bool> Attempt(PostTransactionRequest request)
        {
            try
            {
                await _service.PostTransaction(request);
                return true;
            }
            catch (ValidationException)
            {
                return false;
            }
        }
    }
}
