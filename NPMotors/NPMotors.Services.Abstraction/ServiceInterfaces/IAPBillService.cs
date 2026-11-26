using NPMotors.Repository.Models.Dto;

namespace NPMotors.Services.Abstractions.ServiceInterfaces
{
    public interface IAPBillService
    {
        Task<ApBillResponseDto?> RecordVendorPayment(long apBillId, VendorPaymentCreateDto payment);
        Task<ApBillResponseDto?> GetPaymentHistory(long apBillId);
    }
}
