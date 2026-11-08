using System.ComponentModel.DataAnnotations;
using System.Data;
using NPMotors.Repository.Interface;
using NPMotors.Repository.Models;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.Models.Enums;
using NPMotors.Services.Abstractions.ServiceInterfaces;

namespace NPMotors.Services.Service
{
    public class TransferService : ITransferService
    {
        private readonly IBankAccountRepository _bankAccountRepository;
        private readonly ITransactionRepository _transactionRepository;
        private readonly ITransactionPostingService _transactionPostingService;
        private readonly IRepositoryContext _context;

        public TransferService(
            IBankAccountRepository bankAccountRepository,
            ITransactionRepository transactionRepository,
            ITransactionPostingService transactionPostingService,
            IRepositoryContext context)
        {
            _bankAccountRepository = bankAccountRepository;
            _transactionRepository = transactionRepository;
            _transactionPostingService = transactionPostingService;
            _context = context;
        }

        public async Task<TransferResponseDto> CreateTransfer(CreateTransferRequest request)
        {
            using IDbConnection connection = _context.GetDatabaseConnection();
            if (connection.State != ConnectionState.Open)
                connection.Open();
            using IDbTransaction tx = connection.BeginTransaction();

            try
            {
                // Lock both rows, lower id first, so A->B and B->A cannot deadlock.
                var lowerId = Math.Min(request.SourceAccountId, request.DestinationAccountId);
                var higherId = Math.Max(request.SourceAccountId, request.DestinationAccountId);
                var lower = await _bankAccountRepository.GetBankAccountForUpdate(lowerId, tx);
                var higher = lowerId == higherId
                    ? lower
                    : await _bankAccountRepository.GetBankAccountForUpdate(higherId, tx);

                var source = request.SourceAccountId == lowerId ? lower : higher;
                var destination = request.DestinationAccountId == lowerId ? lower : higher;

                ValidateTransfer(request, source, destination);

                // The reference is built from the outgoing transaction's id, reserved up front.
                var outTransactionId = await _transactionRepository.GetNextTransactionId(tx);
                var reference = GenerateTransferReference(request.Date, outTransactionId);

                await _transactionPostingService.PostTransaction(new PostTransactionRequest
                {
                    TransactionId = outTransactionId,
                    BankAccountId = source!.Id,
                    Direction = Direction.Out,
                    Amount = request.Amount,
                    Date = request.Date,
                    Type = TransactionType.Transfer,
                    SourceNumber = reference,
                    TransferReference = reference,
                    Party = destination!.Name,
                    Description = request.Description,
                    Status = TransactionStatus.Posted
                }, tx);

                var inTransactionId = await _transactionPostingService.PostTransaction(new PostTransactionRequest
                {
                    BankAccountId = destination.Id,
                    Direction = Direction.In,
                    Amount = request.Amount,
                    Date = request.Date,
                    Type = TransactionType.Transfer,
                    SourceNumber = reference,
                    TransferReference = reference,
                    Party = source.Name,
                    Description = request.Description,
                    Status = TransactionStatus.Posted
                }, tx);

                tx.Commit();

                return new TransferResponseDto
                {
                    Transfer_Reference = reference,
                    Out_Transaction_Id = outTransactionId,
                    In_Transaction_Id = inTransactionId,
                    Amount = request.Amount,
                    Date = request.Date,
                    Source_Account_Name = source.Name,
                    Source_Book_Balance = source.BookBalance - request.Amount,
                    Destination_Account_Name = destination.Name,
                    Destination_Book_Balance = destination.BookBalance + request.Amount
                };
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }

        private static void ValidateTransfer(CreateTransferRequest request, BankAccount? source, BankAccount? destination)
        {
            var errors = new List<string>();

            if (source is null)
                errors.Add("The source account does not exist.");
            else if (source.Status != Status.Active)
                errors.Add("The source account must be active.");

            if (destination is null)
                errors.Add("The destination account does not exist.");
            else if (destination.Status != Status.Active)
                errors.Add("The destination account must be active.");

            if (request.SourceAccountId == request.DestinationAccountId)
                errors.Add("The source and destination accounts must be different.");

            if (request.Amount <= 0)
                errors.Add("Amount must be greater than zero.");
            else if (source is not null && request.Amount > source.BookBalance)
                errors.Add("Amount cannot exceed the source account's book balance.");

            if (errors.Count > 0)
                throw new ValidationException(string.Join(" ", errors));
        }

        private static string GenerateTransferReference(DateTime date, long outTransactionId)
        {
            return $"TRF-{date.Year}-{outTransactionId:D6}";
        }
    }
}
