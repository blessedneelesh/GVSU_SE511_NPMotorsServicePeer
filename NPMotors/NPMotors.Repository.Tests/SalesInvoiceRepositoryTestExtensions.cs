using System.Data;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.Repositories;

namespace NPMotors.Repository.Tests
{
    public static class SalesInvoiceRepositoryTestExtensions
    {
        // CancelSalesInvoice runs inside the caller's transaction (the service owns it);
        // this commits on success and rolls back when the repository throws.
        public static async Task<SalesInvoiceResponseDto> CancelCommittedAsync(
            this SalesInvoiceRepository repository, long id, SalesInvoiceCancelDto reason)
        {
            using IDbConnection connection = TestBase._repositoryContext.GetDatabaseConnection();
            if (connection.State != ConnectionState.Open)
                connection.Open();
            using IDbTransaction tx = connection.BeginTransaction();
            try
            {
                var result = await repository.CancelSalesInvoice(id, reason, tx);
                tx.Commit();
                return result;
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }
    }
}
