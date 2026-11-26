using System.ComponentModel.DataAnnotations;
using NPMotors.Repository.Interface;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.RequestFeatures;
using NPMotors.Services.Abstractions.ServiceInterfaces;

namespace NPMotors.Services.Service
{
    public class TransactionService : ITransactionService
    {
        private readonly ITransactionRepository _transactionRepository;

        private const int MaxReasonLength = 500;

        public TransactionService(ITransactionRepository transactionRepository)
        {
            _transactionRepository = transactionRepository;
        }

        public Task<PagedList<TransactionResponseDto>> GetAllTransactions(TransactionParameters parameters)
        {
            return _transactionRepository.GetAllTransactions(parameters);
        }

        public Task<TransactionResponseDto?> GetTransactionById(long id)
        {
            return _transactionRepository.GetTransactionById(id);
        }

        // Returns the new reversal transaction. A missing transaction throws KeyNotFoundException (controller: 404);
        // a broken business rule throws ValidationException (controller: 400). Nothing is saved before all checks pass.
        public async Task<TransactionResponseDto> ReverseTransaction(long id, ReverseTransactionDto reversal)
        {
            var reason = reversal?.Reason?.Trim();
            if (string.IsNullOrEmpty(reason))
                throw new ValidationException("A reversal reason is required.");
            if (reason.Length > MaxReasonLength)
                throw new ValidationException($"The reversal reason cannot exceed {MaxReasonLength} characters.");

            var source = await _transactionRepository.GetTransactionForReversal(id)
                ?? throw new KeyNotFoundException($"Transaction {id} was not found.");

            EnsureNotReversed(source);
            var direction = GetReverseDirection(source.Direction);
            var restored = RestoreSourceBalances(source);

            return await _transactionRepository.ReverseTransaction(new ReversalPostingDto
            {
                Original_Transaction_Id = id,
                Reason = reason,
                Direction = direction,
                Amount = source.Amount,
                Bank_Account_Id = source.Bank_Account_Id,
                Source_Type = source.Source_Type,
                Source_Id = source.Source_Id,
                Source_Number = source.Source_Number,
                Restored_Documents = restored
            });
        }

        // Only a posted receipt or payment that is not itself a reversal and has not been reversed yet.
        private static void EnsureNotReversed(ReversalSourceDto source)
        {
            if (!string.Equals(source.Status, "posted", StringComparison.OrdinalIgnoreCase))
                throw new ValidationException("Only a posted transaction can be reversed.");
            if (source.Source_Type != "Receipt" && source.Source_Type != "Payment")
                throw new ValidationException("Only a receipt or a payment can be reversed.");
            if (source.Reverses_Transaction_Id is not null)
                throw new ValidationException("A reversal cannot be reversed.");
            if (source.Reversed_By_Transaction_Id is not null)
                throw new ValidationException("This transaction has already been reversed.");
        }

        // A reversed receipt ("in") goes out; a reversed payment ("out") comes in.
        private static string GetReverseDirection(string direction)
        {
            return direction?.Trim().ToLowerInvariant() switch
            {
                "in" => "out",
                "out" => "in",
                _ => throw new ValidationException("Direction must be in or out.")
            };
        }

        // Paid = Paid - source.Amount, Balance = Balance + source.Amount, Status = "Unpaid" when Paid = 0, else "Partial".
        // A fully paid sale has only a SalesInvoice and a vehicle paid in full has no APBill, so the list may be empty.
        private static List<SourceBalanceDto> RestoreSourceBalances(ReversalSourceDto source)
        {
            return source.Linked_Documents.Select(document =>
            {
                var paid = document.Paid - source.Amount;
                return new SourceBalanceDto
                {
                    Document_Type = document.Document_Type,
                    Document_Id = document.Document_Id,
                    Amount = document.Amount,
                    Paid = paid,
                    Balance = document.Balance + source.Amount,
                    Status = paid == 0 ? "Unpaid" : "Partial"
                };
            }).ToList();
        }
    }
}
