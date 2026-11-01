using System;
using System.Collections.Generic;
using System.Text;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.RequestFeatures;

namespace NPMotors.Repository.Interface
{
    public interface IBankAccountRepository
    {
        Task<PagedList<BankAccountResponseDto>> GetAllBankAccounts(BankAccountParameters bankAccountParameters);
        Task<BankAccountResponseDto?> GetBankAccountById(long id);
        Task<BankAccountResponseDto> AddBankAccount(BankAccountCreateDto bankAccount);
        Task<BankAccountResponseDto?> UpdateBankAccount(long id, BankAccountUpdateDto bankAccount);
        Task<bool> DeleteBankAccount(long id);
        Task<bool> IsBankAccountReferenced(long id);
        Task<bool> BankExists(long bankId);
    }
}
