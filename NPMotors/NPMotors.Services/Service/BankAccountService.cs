using NPMotors.Services.Abstractions.ServiceInterfaces;
using System;
using System.Collections.Generic;
using System.Text;
using NPMotors.Repository.Interface;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.RequestFeatures;

namespace NPMotors.Services.Service
{
    public class BankAccountService : IBankAccountService
    {
        private readonly IBankAccountRepository _bankAccountRepository;

        private static readonly string[] AllowedTypes = { "Checking", "Savings", "Other" };

        public BankAccountService(IBankAccountRepository bankAccountRepository)
        {
            _bankAccountRepository = bankAccountRepository;
        }

        public Task<PagedList<BankAccountResponseDto>> GetAllBankAccounts(BankAccountParameters bankAccountParameters)
        {
            return _bankAccountRepository.GetAllBankAccounts(bankAccountParameters);
        }

        public Task<BankAccountResponseDto?> GetBankAccountById(long id)
        {
            return _bankAccountRepository.GetBankAccountById(id);
        }

        public async Task<BankAccountResponseDto> AddBankAccount(BankAccountCreateDto bankAccount)
        {
            bankAccount.Type = NormalizeType(bankAccount.Type);
            Validate(bankAccount.Name);
            if (bankAccount.OpeningBalance < 0)
                throw new ArgumentException("Opening balance cannot be negative.");
            await EnsureBankExists(bankAccount.BankId);
            return await _bankAccountRepository.AddBankAccount(bankAccount);
        }

        public async Task<BankAccountResponseDto?> UpdateBankAccount(long id, BankAccountUpdateDto bankAccount)
        {
            bankAccount.Type = NormalizeType(bankAccount.Type);
            Validate(bankAccount.Name);
            await EnsureBankExists(bankAccount.BankId);
            return await _bankAccountRepository.UpdateBankAccount(id, bankAccount);
        }

        public async Task<DeleteBankAccountResult> DeleteBankAccount(long id)
        {
            if (await _bankAccountRepository.IsBankAccountReferenced(id))
            {
                return DeleteBankAccountResult.Referenced;
            }

            return await _bankAccountRepository.DeleteBankAccount(id)
                ? DeleteBankAccountResult.Deleted
                : DeleteBankAccountResult.NotFound;
        }

        private async Task EnsureBankExists(long bankId)
        {
            if (!await _bankAccountRepository.BankExists(bankId))
                throw new ArgumentException("A bank account must belong to an existing bank.");
        }

        private static void Validate(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Account name is required.");
        }

        private static string NormalizeType(string? type)
        {
            var match = AllowedTypes.FirstOrDefault(t => t.Equals(type?.Trim(), StringComparison.OrdinalIgnoreCase));
            return match ?? throw new ArgumentException("Type must be 'Checking', 'Savings' or 'Other'.");
        }
    }
}
