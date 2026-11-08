using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.RequestFeatures;

namespace NPMotors.Services.Abstractions.ServiceInterfaces
{
    public interface IArService
    {
        Task<PagedList<ArAgingDto>> GetArAging(ArAgingParameters parameters);
    }
}
