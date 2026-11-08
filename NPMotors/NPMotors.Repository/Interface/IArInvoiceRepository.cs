using System.Data;
using NPMotors.Repository.Models;

namespace NPMotors.Repository.Interface
{
    public interface IArInvoiceRepository
    {
        Task<long> InsertArInvoice(ArInvoice arInvoice, IDbTransaction tx);
        Task<ArInvoice?> GetArInvoiceById(long id);
        Task<int> UpdateArPayment(ArInvoice ar, decimal expectedBalance, IDbTransaction tx);
    }
}
