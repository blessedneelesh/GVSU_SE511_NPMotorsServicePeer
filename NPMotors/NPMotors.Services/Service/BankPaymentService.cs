using System.ComponentModel.DataAnnotations;
using NPMotors.Repository.Interface;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.Models.Enums;
using NPMotors.Services.Abstractions.ServiceInterfaces;

namespace NPMotors.Services.Service
{
    public class BankPaymentService : IBankPaymentService
    {
        private readonly IBankPaymentRepository _bankPaymentRepository;

        public BankPaymentService(IBankPaymentRepository bankPaymentRepository)
        {
            _bankPaymentRepository = bankPaymentRepository;
        }

        // Validates the payment, then the repository does the bank account check and the atomic save.
        // A ValidationException (from here or the repository) is not caught: the controller returns it as 400.
        public async Task<BankPaymentResponseDto> RecordMiscellaneousPayment(MiscellaneousPaymentCreateDto payment)
        {
            ValidateMiscPayment(payment);
            return await _bankPaymentRepository.RecordMiscellaneousPayment(payment);
        }

        // Payee and Payment_Category not blank; Amount > 0; Payment_Method is Cash | Check | Card | Transfer;
        // Bank_Account_Id given; the category is an operating expense.
        private static void ValidateMiscPayment(MiscellaneousPaymentCreateDto payment)
        {
            if (string.IsNullOrWhiteSpace(payment.Payee))
                throw new ValidationException("Payee is required.");
            if (string.IsNullOrWhiteSpace(payment.Payment_Category))
                throw new ValidationException("Payment category is required.");
            if (payment.Amount <= 0)
                throw new ValidationException("Amount must be greater than zero.");
            var method = payment.Payment_Method?.Trim();
            if (method is null || !Enum.GetNames<PaymentMethod>().Contains(method, StringComparer.OrdinalIgnoreCase))
                throw new ValidationException("Payment method must be Cash, Check, Card or Transfer.");
            if (payment.Bank_Account_Id <= 0)
                throw new ValidationException("A bank account is required.");
            if (!IsOperatingExpense(payment.Payment_Category))
                throw new ValidationException("Inventory purchases and AP settlements are not operating expenses.");
        }

        // False for the inventory-purchase and AP-settlement categories (handled by Stories 19-20),
        // true for operating expenses such as Rent, Utilities, Supplies, Payroll.
        private static bool IsOperatingExpense(string paymentCategory)
        {
            var category = paymentCategory.Trim();
            return !category.Equals("Vehicle Purchase", StringComparison.OrdinalIgnoreCase)
                && !category.Equals("AP Payment", StringComparison.OrdinalIgnoreCase);
        }
    }
}
