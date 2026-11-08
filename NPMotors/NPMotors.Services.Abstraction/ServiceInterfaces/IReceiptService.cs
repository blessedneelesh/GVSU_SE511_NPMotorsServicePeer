using NPMotors.Repository.Models.Dto;

namespace NPMotors.Services.Abstractions.ServiceInterfaces
{
    public interface IReceiptService
    {
        Task<ArInvoiceResponseDto?> RecordArPayment(long arInvoiceId, RecordArPaymentRequest request);
        Task<ArInvoiceResponseDto?> GetPaymentHistory(long arInvoiceId);
    }
}
