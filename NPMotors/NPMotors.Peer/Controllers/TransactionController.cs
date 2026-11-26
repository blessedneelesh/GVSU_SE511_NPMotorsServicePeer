using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.RequestFeatures;
using NPMotors.Services.Abstractions.ServiceInterfaces;

namespace NPMotors.Peer.Controllers
{
    [ApiController]
    [Route("[controller]/[action]")]
    public class TransactionController : ControllerBase
    {
        private readonly ITransactionService _transactionService;

        public TransactionController(ITransactionService transactionService)
        {
            _transactionService = transactionService;
        }

        [HttpGet]
        public async Task<IActionResult> GetAllTransactions([FromQuery] TransactionParameters parameters)
        {
            var transactions = await _transactionService.GetAllTransactions(parameters);
            return Ok(new { items = transactions, metaData = transactions.MetaData });
        }

        [HttpGet]
        public async Task<IActionResult> GetTransactionById([FromQuery] long id)
        {
            var transaction = await _transactionService.GetTransactionById(id);
            return transaction is null ? NotFound() : Ok(transaction);
        }

        [HttpPost("{id:long}")]
        public async Task<IActionResult> ReverseTransaction(long id, [FromBody] ReverseTransactionDto reversal)
        {
            try
            {
                var reversalTransaction = await _transactionService.ReverseTransaction(id, reversal);
                return Ok(reversalTransaction);
            }
            catch (KeyNotFoundException)
            {
                return NotFound();
            }
            catch (ValidationException ex)
            {
                return BadRequest(new { errors = new[] { ex.Message } });
            }
        }
    }
}
