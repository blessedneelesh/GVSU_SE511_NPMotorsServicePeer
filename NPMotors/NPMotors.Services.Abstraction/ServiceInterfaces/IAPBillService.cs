using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.RequestFeatures;

namespace NPMotors.Services.Abstractions.ServiceInterfaces
{
    public interface IAPBillService
    {
        Task<ApBillResponseDto?> RecordVendorPayment(long apBillId, VendorPaymentCreateDto payment);
        Task<ApBillResponseDto?> GetPaymentHistory(long apBillId);
        Task<PagedList<ApBillResponseDto>> GetAllApBills(ApBillParameters parameters);
        Task<ApDetailDto?> GetApById(long id);
    }
}
