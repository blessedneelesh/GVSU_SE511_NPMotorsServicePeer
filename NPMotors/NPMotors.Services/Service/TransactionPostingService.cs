using System.ComponentModel.DataAnnotations;
using System.Data;
using NPMotors.Repository.Interface;
using NPMotors.Repository.Models;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.Models.Enums;
using NPMotors.Services.Abstractions.ServiceInterfaces;

namespace NPMotors.Services.Service
{
    public class TransactionPostingService : ITransactionPostingService
    {
        private readonly ITransactionRepository _transactionRepository;
        private readonly IBankAccountRepository _bankAccountRepository;
        private readonly IBankStatementLineRepository _bankStatementLineRepository;
        private readonly IRepositoryContext _context;

        // No user context exists yet, so postings are attributed to the system.
        private const string CreatedBy = "system";

        public TransactionPostingService(
            ITransactionRepository transactionRepository,
            IBankAccountRepository bankAccountRepository,
            IBankStatementLineRepository bankStatementLineRepository,
            IRepositoryContext context)
        {
            _transactionRepository = transactionRepository;
            _bankAccountRepository = bankAccountRepository;
            _bankStatementLineRepository = bankStatementLineRepository;
            _context = context;
        }

        public async Task<long> PostTransaction(PostTransactionRequest request, IDbTransaction? tx = null)
        {
            // Called on its own (tx is null): open one connection and one database
            // transaction here, and pass tx to every repository call.
            IDbConnection? ownConnection = null;
            IDbTransaction? ownTx = null;
            if (tx is null)
            {
                ownConnection = _context.GetDatabaseConnection();
                if (ownConnection.State != ConnectionState.Open)
                    ownConnection.Open();
                ownTx = ownConnection.BeginTransaction();
                tx = ownTx;
            }

            try
            {
                var account = await _bankAccountRepository.GetBankAccountForUpdate(request.BankAccountId, tx);

                ValidateTransaction(request, account);
                if (request.Status == TransactionStatus.Posted && request.Direction == Direction.Out)
                    EnsureSufficientBalance(account!, request.Amount);

                var transaction = new Transaction
                {
                    BankAccountId = request.BankAccountId,
                    Type = request.Type,
                    SourceNumber = request.SourceNumber.Trim(),
                    Direction = request.Direction,
                    Date = request.Date,
                    Party = request.Party,
                    Amount = request.Amount,
                    Description = request.Description,
                    Status = request.Status,
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = CreatedBy
                };
                var transactionId = await _transactionRepository.InsertTransaction(transaction, tx);

                // Draft transactions never change a balance.
                if (request.Status == TransactionStatus.Posted)
                {
                    var newBalance = ApplyToBookBalance(account!.BookBalance, request.Direction, request.Amount);
                    await _bankAccountRepository.UpdateBookBalance(account.Id, newBalance, tx);

                    // Every posted transaction gets exactly one simulated statement line, saved
                    // in the same database transaction. The account row is locked, so no other
                    // posting can read the same previous running balance.
                    transaction.Id = transactionId;
                    var line = BuildStatementLine(transaction);
                    var previousBalance = await _bankStatementLineRepository.GetLastRunningBalance(account.Id, tx);
                    line.RunningBalance = CalculateRunningBalance(
                        previousBalance ?? account.OpeningBalance, line.Deposit, line.Withdrawal);
                    await _bankStatementLineRepository.InsertStatementLine(line, tx);
                }

                ownTx?.Commit();
                return transactionId;
            }
            catch
            {
                ownTx?.Rollback();
                throw;
            }
            finally
            {
                ownTx?.Dispose();
                ownConnection?.Dispose();
            }
        }

        private static void ValidateTransaction(PostTransactionRequest request, BankAccount? account)
        {
            var errors = new List<string>();

            if (account is null)
                errors.Add("The bank account does not exist.");
            else if (account.Status != Status.Active)
                errors.Add("The bank account must be active.");

            if (!Enum.IsDefined(request.Direction))
                errors.Add("Direction must be In or Out.");
            if (request.Amount <= 0)
                errors.Add("Amount must be greater than zero.");
            if (!Enum.IsDefined(request.Type))
                errors.Add("Source type is required.");
            if (string.IsNullOrWhiteSpace(request.SourceNumber))
                errors.Add("Source number is required.");
            if (!Enum.IsDefined(request.Status))
                errors.Add("Status must be Draft or Posted.");

            if (errors.Count > 0)
                throw new ValidationException(string.Join(" ", errors));
        }

        private static void EnsureSufficientBalance(BankAccount account, decimal amount)
        {
            if (amount > account.BookBalance)
                throw new ValidationException("An outgoing transaction cannot exceed the account's book balance.");
        }

        private static decimal ApplyToBookBalance(decimal bookBalance, Direction direction, decimal amount)
        {
            return direction == Direction.In ? bookBalance + amount : bookBalance - amount;
        }

        private static BankStatementLine BuildStatementLine(Transaction transaction)
        {
            return new BankStatementLine
            {
                BankAccountId = transaction.BankAccountId,
                TransactionId = transaction.Id,
                PostedDate = transaction.Date,
                Description = transaction.Description,
                Party = transaction.Party,
                Type = transaction.Type,
                Direction = transaction.Direction,
                // Incoming sets a deposit and outgoing a withdrawal, never both.
                Deposit = transaction.Direction == Direction.In ? transaction.Amount : 0m,
                Withdrawal = transaction.Direction == Direction.Out ? transaction.Amount : 0m,
                StatementStatus = StatementStatus.Cleared,
                MatchStatus = MatchStatus.Matched,
                Source = StatementSource.Simulated,
                CreatedAt = DateTime.UtcNow
            };
        }

        private static decimal CalculateRunningBalance(decimal previousRunningBalance, decimal deposit, decimal withdrawal)
        {
            return previousRunningBalance + deposit - withdrawal;
        }
    }
}
