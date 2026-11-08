using NPMotors.Repository.Interface;
using NPMotors.Repository.Models;
using NPMotors.Repository.Models.Enums;
using System;
using System.Collections.Generic;
using System.Data;
using System.Text;
using NPMotors.Repository.Models.Dto;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using NPMotors.Repository.RequestFeatures;

namespace NPMotors.Repository.Repositories
{
    public class BankAccountRepository : IBankAccountRepository
    {
        private readonly IRepositoryContext _repositoryContext;

        public BankAccountRepository(IRepositoryContext repositoryContext)
        {
            _repositoryContext = repositoryContext;
        }

        private static readonly string[] AllowedTypes = { "checking", "savings", "other" };

        private const string FromSql = "bank_account ba JOIN bank b ON b.id = ba.bank_id";

        private const string BankAccountColumns =
            "ba.id, ba.bank_id AS BankId, b.name AS BankName, ba.name, ba.account_number AS AccountNumber, " +
            "ba.type, ba.routing, ba.book_balance AS BookBalance, ba.status";

        // Lists show only the last 4 characters of the account number.
        private const string MaskedBankAccountColumns =
            "ba.id, ba.bank_id AS BankId, b.name AS BankName, ba.name, " +
            "CASE WHEN ba.account_number IS NULL THEN NULL " +
            "WHEN LENGTH(ba.account_number) <= 4 THEN REPEAT('*', LENGTH(ba.account_number)) " +
            "ELSE REPEAT('*', LENGTH(ba.account_number) - 4) || RIGHT(ba.account_number, 4) END AS AccountNumber, " +
            "ba.type, ba.routing, ba.book_balance AS BookBalance, ba.status";

        public async Task<PagedList<BankAccountResponseDto>> GetAllBankAccounts(BankAccountParameters bankAccountParameters)
        {
            using IDbConnection connection = _repositoryContext.GetDatabaseConnection();

            var whereClauses = new List<string>();
            var queryParams = new DynamicParameters();

            if (bankAccountParameters.Status.HasValue)
            {
                whereClauses.Add("LOWER(ba.status) = @Status");
                queryParams.Add("Status", bankAccountParameters.Status.Value.ToDbValue());
            }

            var type = bankAccountParameters.Type?.Trim();
            if (!string.IsNullOrWhiteSpace(type) &&
                AllowedTypes.Contains(type, StringComparer.OrdinalIgnoreCase))
            {
                whereClauses.Add("LOWER(ba.type) = @Type");
                queryParams.Add("Type", type.ToLowerInvariant());
            }

            if (bankAccountParameters.BankId.HasValue)
            {
                whereClauses.Add("ba.bank_id = @BankId");
                queryParams.Add("BankId", bankAccountParameters.BankId.Value);
            }

            var searchTerm = bankAccountParameters.SearchTerm?.Trim();
            if (!string.IsNullOrWhiteSpace(searchTerm))
            {
                whereClauses.Add(@"(
                    LOWER(ba.name) LIKE @SearchTerm OR
                    LOWER(b.name) LIKE @SearchTerm OR
                    LOWER(ba.type) LIKE @SearchTerm
                )");
                queryParams.Add("SearchTerm", $"%{searchTerm.ToLowerInvariant()}%");
            }

            var whereSql = whereClauses.Count > 0
                ? " WHERE " + string.Join(" AND ", whereClauses)
                : string.Empty;

            var count = await connection.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM {FromSql}{whereSql}", queryParams);

            queryParams.Add("Offset", (bankAccountParameters.PageNumber - 1) * bankAccountParameters.PageSize);
            queryParams.Add("Limit", bankAccountParameters.PageSize);

            var bankAccounts = (await connection.QueryAsync<BankAccountResponseDto>(
                $"SELECT {MaskedBankAccountColumns} FROM {FromSql}{whereSql} ORDER BY ba.name, ba.id OFFSET @Offset LIMIT @Limit",
                queryParams)).ToList();

            return new PagedList<BankAccountResponseDto>(
                bankAccounts,
                count,
                bankAccountParameters.PageNumber,
                bankAccountParameters.PageSize);
        }

        public async Task<BankAccountResponseDto?> GetBankAccountById(long id)
        {
            using IDbConnection connection = _repositoryContext.GetDatabaseConnection();

            return await connection.QuerySingleOrDefaultAsync<BankAccountResponseDto>(
                $"SELECT {BankAccountColumns} FROM {FromSql} WHERE ba.id = @Id",
                new { Id = id });
        }

        public async Task<BankAccountResponseDto> AddBankAccount(BankAccountCreateDto bankAccount)
        {
            using IDbConnection connection = _repositoryContext.GetDatabaseConnection();

            var queryParams = new DynamicParameters();
            queryParams.Add("BankId", bankAccount.BankId);
            queryParams.Add("Name", bankAccount.Name.Trim());
            queryParams.Add("AccountNumber", bankAccount.AccountNumber?.Trim());
            queryParams.Add("Routing", bankAccount.Routing?.Trim());
            queryParams.Add("Type", bankAccount.Type.Trim());
            queryParams.Add("BookBalance", bankAccount.OpeningBalance);
            queryParams.Add("Status", bankAccount.Status.ToDbValue());

            // The book balance starts equal to the opening balance.
            return await connection.QuerySingleAsync<BankAccountResponseDto>(
                $@"WITH ba AS (
                       INSERT INTO bank_account (bank_id, name, account_number, routing, type, book_balance, status)
                       VALUES (@BankId, @Name, @AccountNumber, @Routing, @Type, @BookBalance, @Status)
                       RETURNING *
                   )
                   SELECT {BankAccountColumns} FROM ba JOIN bank b ON b.id = ba.bank_id",
                queryParams);
        }

        public async Task<BankAccountResponseDto?> UpdateBankAccount(long id, BankAccountUpdateDto bankAccount)
        {
            using IDbConnection connection = _repositoryContext.GetDatabaseConnection();

            var queryParams = new DynamicParameters();
            queryParams.Add("Id", id);
            queryParams.Add("BankId", bankAccount.BankId);
            queryParams.Add("Name", bankAccount.Name.Trim());
            queryParams.Add("AccountNumber", bankAccount.AccountNumber?.Trim());
            queryParams.Add("Routing", bankAccount.Routing?.Trim());
            queryParams.Add("Type", bankAccount.Type.Trim());
            queryParams.Add("Status", bankAccount.Status.ToDbValue());

            // book_balance is never updated here.
            return await connection.QuerySingleOrDefaultAsync<BankAccountResponseDto>(
                $@"WITH ba AS (
                       UPDATE bank_account
                       SET bank_id = @BankId, name = @Name, account_number = @AccountNumber,
                           routing = @Routing, type = @Type, status = @Status
                       WHERE id = @Id
                       RETURNING *
                   )
                   SELECT {BankAccountColumns} FROM ba JOIN bank b ON b.id = ba.bank_id",
                queryParams);
        }

        public async Task<bool> DeleteBankAccount(long id)
        {
            using IDbConnection connection = _repositoryContext.GetDatabaseConnection();

            var rowsAffected = await connection.ExecuteAsync(
                "DELETE FROM bank_account WHERE id = @Id",
                new { Id = id });

            return rowsAffected > 0;
        }

        public async Task<bool> IsBankAccountReferenced(long id)
        {
            using IDbConnection connection = _repositoryContext.GetDatabaseConnection();

            return await connection.ExecuteScalarAsync<bool>(
                @"SELECT EXISTS (SELECT 1 FROM ""transaction"" WHERE bank_account_id = @Id)
                      OR EXISTS (SELECT 1 FROM bank_payment WHERE bank_account_id = @Id)
                      OR EXISTS (SELECT 1 FROM bank_receipt WHERE bank_account_id = @Id)
                      OR EXISTS (SELECT 1 FROM bank_statement_line WHERE bank_account_id = @Id)",
                new { Id = id });
        }

        public async Task<bool> BankExists(long bankId)
        {
            using IDbConnection connection = _repositoryContext.GetDatabaseConnection();

            return await connection.ExecuteScalarAsync<bool>(
                "SELECT EXISTS (SELECT 1 FROM bank WHERE id = @Id)",
                new { Id = bankId });
        }

        // Locks the row until tx ends, so two postings cannot both pass the balance check.
        public async Task<BankAccount?> GetBankAccountForUpdate(long id, IDbTransaction tx)
        {
            return await tx.Connection!.QuerySingleOrDefaultAsync<BankAccount>(
                @"SELECT id, name, opening_balance AS OpeningBalance, book_balance AS BookBalance, LOWER(status) AS Status
                  FROM bank_account WHERE id = @Id FOR UPDATE",
                new { Id = id }, tx);
        }

        public async Task<int> UpdateBookBalance(long id, decimal bookBalance, IDbTransaction tx)
        {
            return await tx.Connection!.ExecuteAsync(
                "UPDATE bank_account SET book_balance = @BookBalance WHERE id = @Id",
                new { Id = id, BookBalance = bookBalance }, tx);
        }
    }
}
