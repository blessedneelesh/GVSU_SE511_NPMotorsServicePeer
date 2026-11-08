using NPMotors.Repository.Models.Dto;

namespace NPMotors.Services.Abstractions.ServiceInterfaces
{
    public interface ITransferService
    {
        Task<TransferResponseDto> CreateTransfer(CreateTransferRequest request);
    }
}
