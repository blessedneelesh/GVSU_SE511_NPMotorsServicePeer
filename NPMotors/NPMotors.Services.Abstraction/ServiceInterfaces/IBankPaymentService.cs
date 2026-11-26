using NPMotors.Repository.Models.Dto;

namespace NPMotors.Services.Abstractions.ServiceInterfaces
{
    public interface IBankPaymentService
    {
        Task<BankPaymentResponseDto> RecordMiscellaneousPayment(MiscellaneousPaymentCreateDto payment);
    }
}
