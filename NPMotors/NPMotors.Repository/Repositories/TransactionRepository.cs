using System.Data;
using Dapper;
using NPMotors.Repository.Interface;
using NPMotors.Repository.Models;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.Models.Enums;
using NPMotors.Repository.RequestFeatures;

namespace NPMotors.Repository.Repositories
{
    public class TransactionRepository : ITransactionRepository
    {
        private readonly IRepositoryContext _context;

        public TransactionRepository(IRepositoryContext context)
        {
            _context = context;
        }

        private const string FromSql = "\"transaction\" t JOIN bank_account ba ON ba.id = t.bank_account_id";

        private const string TransactionColumns =
            "t.id, t.number, t.bank_account_id, ba.name AS bank_account_name, t.type, t.source_number, " +
            "t.direction::text AS direction, t.date::timestamp AS date, t.party, t.amount, t.description, " +
            "t.status, t.created_at, t.created_by";

        public async Task<long> InsertTransaction(Transaction transaction, IDbTransaction tx)
        {
            var queryParams = new DynamicParameters();
            queryParams.Add("BankAccountId", transaction.BankAccountId);
            queryParams.Add("NumberPrefix", $"TXN-{transaction.CreatedAt.Year}-");
            queryParams.Add("Type", transaction.Type.ToString());
            queryParams.Add("SourceNumber", transaction.SourceNumber);
            queryParams.Add("Direction", transaction.Direction.ToDbValue());
            queryParams.Add("Date", transaction.Date.Date, DbType.Date);
            queryParams.Add("Party", transaction.Party);
            queryParams.Add("Amount", transaction.Amount);
            queryParams.Add("Description", transaction.Description);
            queryParams.Add("Status", transaction.Status.ToDbValue());
            queryParams.Add("CreatedAt", transaction.CreatedAt);
            queryParams.Add("CreatedBy", transaction.CreatedBy);

            // The number is built from the row id (TXN-<year>-<id padded to 6>), so the id is
            // taken from the table's identity sequence first and inserted explicitly.
            return await tx.Connection!.ExecuteScalarAsync<long>(
                @"WITH next_id AS (SELECT nextval(pg_get_serial_sequence('""transaction""', 'id')) AS id)
                  INSERT INTO ""transaction"" (id, bank_account_id, number, type, source_number, direction, date, party, amount, description, status, created_at, created_by)
                  SELECT n.id, @BankAccountId, @NumberPrefix || LPAD(n.id::text, 6, '0'), @Type, @SourceNumber, @Direction::direction_enum, @Date, @Party, @Amount, @Description, @Status, @CreatedAt, @CreatedBy
                  FROM next_id n
                  RETURNING id",
                queryParams, tx);
        }

        public async Task<PagedList<TransactionResponseDto>> GetAllTransactions(TransactionParameters parameters)
        {
            using IDbConnection connection = _context.GetDatabaseConnection();

            var whereClauses = new List<string>();
            var queryParams = new DynamicParameters();

            if (parameters.BankAccountId.HasValue)
            {
                whereClauses.Add("t.bank_account_id = @BankAccountId");
                queryParams.Add("BankAccountId", parameters.BankAccountId.Value);
            }

            var direction = parameters.Direction?.Trim().ToLowerInvariant();
            if (direction == "in" || direction == "out")
            {
                whereClauses.Add("t.direction = @Direction::direction_enum");
                queryParams.Add("Direction", direction);
            }

            if (parameters.DateFrom.HasValue)
            {
                whereClauses.Add("t.date >= @DateFrom");
                queryParams.Add("DateFrom", parameters.DateFrom.Value.Date, DbType.Date);
            }

            if (parameters.DateTo.HasValue)
            {
                whereClauses.Add("t.date <= @DateTo");
                queryParams.Add("DateTo", parameters.DateTo.Value.Date, DbType.Date);
            }

            var whereSql = whereClauses.Count > 0
                ? " WHERE " + string.Join(" AND ", whereClauses)
                : string.Empty;

            var count = await connection.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM {FromSql}{whereSql}", queryParams);

            queryParams.Add("Offset", (parameters.PageNumber - 1) * parameters.PageSize);
            queryParams.Add("Limit", parameters.PageSize);

            var transactions = (await connection.QueryAsync<TransactionResponseDto>(
                $"SELECT {TransactionColumns} FROM {FromSql}{whereSql} ORDER BY t.date DESC, t.id DESC OFFSET @Offset LIMIT @Limit",
                queryParams)).ToList();

            return new PagedList<TransactionResponseDto>(transactions, count, parameters.PageNumber, parameters.PageSize);
        }

        public async Task<TransactionResponseDto?> GetTransactionById(long id)
        {
            using IDbConnection connection = _context.GetDatabaseConnection();

            return await connection.QuerySingleOrDefaultAsync<TransactionResponseDto>(
                $"SELECT {TransactionColumns} FROM {FromSql} WHERE t.id = @Id",
                new { Id = id });
        }
    }
}
