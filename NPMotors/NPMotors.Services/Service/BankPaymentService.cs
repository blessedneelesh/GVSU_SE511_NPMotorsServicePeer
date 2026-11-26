using NPMotors.Repository.Interface;
using NPMotors.Repository.Models.Dto;
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

        // Maps the DTO to the repository call; a business-rule violation (ValidationException) from the
        // repository is not caught here, the controller returns it as 400.
        public async Task<BankPaymentResponseDto> RecordMiscellaneousPayment(MiscellaneousPaymentCreateDto payment)
        {
            return await _bankPaymentRepository.RecordMiscellaneousPayment(payment);
        }
    }
}
