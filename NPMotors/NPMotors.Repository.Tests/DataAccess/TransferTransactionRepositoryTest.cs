using Dapper;
using Npgsql;
using NPMotors.Repository.Models;
using NPMotors.Repository.Models.Enums;
using NPMotors.Repository.Repositories;

namespace NPMotors.Repository.Tests.DataAccess
{
    // Story 10 additions to TransactionRepository: reserved ids and the transfer_reference column.
    public class TransferTransactionRepositoryTest : TestBase, IAsyncLifetime
    {
        private readonly TransactionRepository _repository;
        private readonly string _marker = $"itest-{Guid.NewGuid():N}".Substring(0, 18);
        private long _bankId;
        private long _accountId;

        public TransferTransactionRepositoryTest()
        {
            _repository = new TransactionRepository(_repositoryContext);
        }

        public async Task InitializeAsync()
        {
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

        private Transaction NewTransaction(
            Direction direction = Direction.Out, TransactionType type = TransactionType.Transfer,
            string? transferReference = null, long id = 0) => new()
        {
            Id = id,
            BankAccountId = _accountId,
            Type = type,
            SourceNumber = "SRC-1",
            Direction = direction,
            Date = new DateTime(2026, 11, 7),
            Amount = 25m,
            Status = TransactionStatus.Posted,
            CreatedAt = DateTime.UtcNow,
            CreatedBy = "itest",
            TransferReference = transferReference
        };

        private async Task<long> InsertAsync(Transaction transaction)
        {
            using var connection = _repositoryContext.GetDatabaseConnection();
            using var tx = connection.BeginTransaction();
            var id = await _repository.InsertTransaction(transaction, tx);
            tx.Commit();
            return id;
        }

        private async Task<string?> StoredReferenceAsync(long id)
        {
            using var connection = _repositoryContext.GetDatabaseConnection();
            return await connection.ExecuteScalarAsync<string?>(
                @"SELECT transfer_reference FROM ""transaction"" WHERE id = @Id", new { Id = id });
        }

        // ---- GetNextTransactionId ----

        [Fact]
        public async Task GetNextTransactionId_ReturnsIncreasingIds()
        {
            using var connection = _repositoryContext.GetDatabaseConnection();
            using var tx = connection.BeginTransaction();

            var first = await _repository.GetNextTransactionId(tx);
            var second = await _repository.GetNextTransactionId(tx);
            tx.Rollback();

            Assert.True(first > 0);
            Assert.True(second > first);
        }

        [Fact]
        public async Task GetNextTransactionId_IdIsNotReusedByLaterInsert()
        {
            using var connection = _repositoryContext.GetDatabaseConnection();
            using var tx = connection.BeginTransaction();
            var reserved = await _repository.GetNextTransactionId(tx);
            tx.Rollback();

            var inserted = await InsertAsync(NewTransaction());

            Assert.NotEqual(reserved, inserted);
        }

        // ---- InsertTransaction with a reserved id ----

        [Fact]
        public async Task InsertTransaction_ReservedId_IsUsedAsRowIdAndInNumber()
        {
            long reserved;
            using (var connection = _repositoryContext.GetDatabaseConnection())
            using (var tx = connection.BeginTransaction())
            {
                reserved = await _repository.GetNextTransactionId(tx);
                var id = await _repository.InsertTransaction(NewTransaction(id: reserved), tx);
                tx.Commit();
                Assert.Equal(reserved, id);
            }

            var found = await _repository.GetTransactionById(reserved);

            Assert.NotNull(found);
            Assert.Equal($"TXN-{DateTime.UtcNow.Year}-{reserved:D6}", found!.Number);
        }

        [Fact]
        public async Task InsertTransaction_NoReservedId_StillGetsDatabaseId()
        {
            var id = await InsertAsync(NewTransaction(id: 0));

            Assert.True(id > 0);
            Assert.NotNull(await _repository.GetTransactionById(id));
        }

        // ---- transfer_reference ----

        [Fact]
        public async Task InsertTransaction_WithTransferReference_StoresIt()
        {
            var id = await InsertAsync(NewTransaction(transferReference: $"{_marker}-R1"));

            Assert.Equal($"{_marker}-R1", await StoredReferenceAsync(id));
        }

        [Fact]
        public async Task InsertTransaction_WithoutTransferReference_StoresNull()
        {
            var id = await InsertAsync(NewTransaction(type: TransactionType.BankReceipt));

            Assert.Null(await StoredReferenceAsync(id));
        }

        [Fact]
        public async Task InsertTransaction_OutAndInWithSameReference_BothSaved()
        {
            var reference = $"{_marker}-R2";

            var outId = await InsertAsync(NewTransaction(Direction.Out, transferReference: reference));
            var inId = await InsertAsync(NewTransaction(Direction.In, transferReference: reference));

            Assert.Equal(reference, await StoredReferenceAsync(outId));
            Assert.Equal(reference, await StoredReferenceAsync(inId));
        }

        [Fact]
        public async Task InsertTransaction_SecondOutgoingWithSameReference_Rejected()
        {
            var reference = $"{_marker}-R3";
            await InsertAsync(NewTransaction(Direction.Out, transferReference: reference));

            await Assert.ThrowsAsync<PostgresException>(
                () => InsertAsync(NewTransaction(Direction.Out, transferReference: reference)));
        }

        [Fact]
        public async Task InsertTransaction_SecondIncomingWithSameReference_Rejected()
        {
            var reference = $"{_marker}-R4";
            await InsertAsync(NewTransaction(Direction.In, transferReference: reference));

            await Assert.ThrowsAsync<PostgresException>(
                () => InsertAsync(NewTransaction(Direction.In, transferReference: reference)));
        }

        [Fact]
        public async Task InsertTransaction_ReferenceOnNonTransferType_Rejected()
        {
            await Assert.ThrowsAsync<PostgresException>(
                () => InsertAsync(NewTransaction(type: TransactionType.BankReceipt, transferReference: $"{_marker}-R5")));
        }
    }
}
