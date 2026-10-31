using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.RequestFeatures;
using NPMotors.Services.Abstractions.ServiceInterfaces;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.Text;
namespace NPMotors.Peer.Controllers
{
    [ApiController]
    [Route("[controller]/[action]")]
    public class BankAccountController : ControllerBase
    {
        private readonly IBankAccountService _bankAccountService;
        public BankAccountController(IBankAccountService bankAccountService)
        {
            _bankAccountService = bankAccountService;
        }
        [HttpGet]
        public async Task<IActionResult> GetAllBankAccounts([FromQuery] BankAccountParameters bankAccountParameters)
        {
            var bankAccounts = await _bankAccountService.GetAllBankAccounts(bankAccountParameters);
            return Ok(new { items = bankAccounts, metaData = bankAccounts.MetaData });
        }

        [HttpGet]
        public async Task<IActionResult> GetBankAccountById([FromQuery] long id)
        {
            var bankAccount = await _bankAccountService.GetBankAccountById(id);
            return bankAccount is null ? NotFound() : Ok(bankAccount);
        }

        [HttpPost]
        public async Task<IActionResult> AddBankAccount([FromBody] BankAccountCreateDto bankAccount)
        {
            try
            {
                var created = await _bankAccountService.AddBankAccount(bankAccount);
                return StatusCode(201, created);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPut]
        public async Task<IActionResult> UpdateBankAccount([FromQuery] long id, [FromBody] BankAccountUpdateDto bankAccount)
        {
            try
            {
                var updated = await _bankAccountService.UpdateBankAccount(id, bankAccount);
                return updated is null ? NotFound() : Ok(updated);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpDelete]
        public async Task<IActionResult> DeleteBankAccount([FromQuery] long id)
        {
            var result = await _bankAccountService.DeleteBankAccount(id);
            return result switch
            {
                DeleteBankAccountResult.Deleted => NoContent(),
                DeleteBankAccountResult.Referenced => Conflict(new { message = "Bank account is referenced by transactions, payments, receipts or statement lines and cannot be deleted. Set the status to inactive instead." }),
                _ => NotFound()
            };
        }
    }
}
