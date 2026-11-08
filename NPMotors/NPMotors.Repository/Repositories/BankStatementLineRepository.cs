using System.Data;
using System.Globalization;
using Dapper;
using NPMotors.Repository.Interface;
using NPMotors.Repository.Models;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.Models.Enums;
using NPMotors.Repository.RequestFeatures;

namespace NPMotors.Repository.Repositories
{
    public class BankStatementLineRepository : IBankStatementLineRepository
    {
        private readonly IRepositoryContext _context;

        public BankStatementLineRepository(IRepositoryContext context)
        {
            _context = context;
        }

        private const string FromSql = "bank_statement_line l JOIN bank_account ba ON ba.id = l.bank_account_id";

        private const string StatementLineColumns =
            "l.id, l.bank_account_id, ba.name AS bank_account_name, l.transaction_id, l.posted_date::timestamp AS posted_date, " +
            "l.bank_reference, l.description, l.party, l.type, l.direction::text AS direction, l.deposit, l.withdrawal, " +
            "l.running_balance, l.statement_status, l.match_status, l.source";

        public async Task<decimal?> GetLastRunningBalance(long bankAccountId, IDbTransaction tx)
        {
            return await tx.Connection!.QueryFirstOrDefaultAsync<decimal?>(
                @"SELECT running_balance FROM bank_statement_line
                  WHERE bank_account_id = @BankAccountId
                  ORDER BY posted_date DESC, id DESC
                  LIMIT 1",
                new { BankAccountId = bankAccountId }, tx);
        }

        public async Task<long> InsertStatementLine(BankStatementLine line, IDbTransaction tx)
        {
            var queryParams = new DynamicParameters();
            queryParams.Add("BankAccountId", line.BankAccountId);
            queryParams.Add("TransactionId", line.TransactionId);
            queryParams.Add("PostedDate", line.PostedDate.Date, DbType.Date);
            queryParams.Add("BankReference", line.BankReference);
            queryParams.Add("Description", line.Description);
            queryParams.Add("Party", line.Party);
            queryParams.Add("Type", line.Type.ToString());
            queryParams.Add("Direction", line.Direction.ToDbValue());
            queryParams.Add("Deposit", line.Deposit);
            queryParams.Add("Withdrawal", line.Withdrawal);
            queryParams.Add("RunningBalance", line.RunningBalance);
            queryParams.Add("StatementStatus", line.StatementStatus.ToString());
            queryParams.Add("MatchStatus", line.MatchStatus.ToString());
            queryParams.Add("Source", line.Source.ToString());
            queryParams.Add("CreatedAt", line.CreatedAt);

            return await tx.Connection!.ExecuteScalarAsync<long>(
                @"INSERT INTO bank_statement_line (bank_account_id, transaction_id, posted_date, bank_reference, description, party, type, direction, deposit, withdrawal, running_balance, statement_status, match_status, source, created_at)
                  VALUES (@BankAccountId, @TransactionId, @PostedDate, @BankReference, @Description, @Party, @Type, @Direction::direction_enum, @Deposit, @Withdrawal, @RunningBalance, @StatementStatus, @MatchStatus, @Source, @CreatedAt)
                  RETURNING id",
                queryParams, tx);
        }

        public async Task<PagedList<StatementLineResponseDto>> GetStatementLines(StatementLineParameters parameters)
        {
            using IDbConnection connection = _context.GetDatabaseConnection();

            var whereClauses = new List<string>();
            var queryParams = new DynamicParameters();

            if (parameters.BankAccountId.HasValue)
            {
                whereClauses.Add("l.bank_account_id = @BankAccountId");
                queryParams.Add("BankAccountId", parameters.BankAccountId.Value);
            }

            if (DateTime.TryParseExact(parameters.Month?.Trim(), "yyyy-MM", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var monthStart))
            {
                whereClauses.Add("l.posted_date >= @MonthStart AND l.posted_date < @NextMonthStart");
                queryParams.Add("MonthStart", monthStart, DbType.Date);
                queryParams.Add("NextMonthStart", monthStart.AddMonths(1), DbType.Date);
            }

            if (Enum.TryParse<TransactionType>(parameters.Type?.Trim(), true, out var type) && Enum.IsDefined(type))
            {
                whereClauses.Add("l.type = @Type");
                queryParams.Add("Type", type.ToString());
            }

            var whereSql = whereClauses.Count > 0
                ? " WHERE " + string.Join(" AND ", whereClauses)
                : string.Empty;

            var count = await connection.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM {FromSql}{whereSql}", queryParams);

            queryParams.Add("Offset", (parameters.PageNumber - 1) * parameters.PageSize);
            queryParams.Add("Limit", parameters.PageSize);

            var lines = (await connection.QueryAsync<StatementLineResponseDto>(
                $"SELECT {StatementLineColumns} FROM {FromSql}{whereSql} ORDER BY l.posted_date, l.id OFFSET @Offset LIMIT @Limit",
                queryParams)).ToList();

            return new PagedList<StatementLineResponseDto>(lines, count, parameters.PageNumber, parameters.PageSize);
        }
    }
}
