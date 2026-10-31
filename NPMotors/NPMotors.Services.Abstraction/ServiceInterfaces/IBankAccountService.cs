using System;
using System.Collections.Generic;
using System.Text;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.RequestFeatures;

namespace NPMotors.Services.Abstractions.ServiceInterfaces
{
    public enum DeleteBankAccountResult
    {
        Deleted,
        NotFound,
        Referenced
    }

    public interface IBankAccountService
    {
        Task<PagedList<BankAccountResponseDto>> GetAllBankAccounts(BankAccountParameters bankAccountParameters);
        Task<BankAccountResponseDto?> GetBankAccountById(long id);
        Task<BankAccountResponseDto> AddBankAccount(BankAccountCreateDto bankAccount);
        Task<BankAccountResponseDto?> UpdateBankAccount(long id, BankAccountUpdateDto bankAccount);
        Task<DeleteBankAccountResult> DeleteBankAccount(long id);
    }
}
