using System;
using System.Collections.Generic;
using System.Text;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.RequestFeatures;

namespace NPMotors.Services.Abstractions.ServiceInterfaces
{
    public interface IPartService
    {
        Task<PagedList<PartResponseDto>> GetAllParts(PartParameters parameters);
        Task<PartResponseDto?> GetPartById(long id);
        Task<PartResponseDto> AddPart(PartCreateDto part);
    }
}
