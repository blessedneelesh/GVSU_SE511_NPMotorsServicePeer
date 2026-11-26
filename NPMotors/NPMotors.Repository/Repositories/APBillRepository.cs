using System.Data;
using Dapper;
using NPMotors.Repository.Interface;
using NPMotors.Repository.Models;

namespace NPMotors.Repository.Repositories
{
    public class APBillRepository : IAPBillRepository
    {
        private readonly IRepositoryContext _context;

        public APBillRepository(IRepositoryContext context)
        {
            _context = context;
        }

        private const string BillColumns =
            "id, number, vendor_id AS VendorId, vehicle_id AS VehicleId, date, due_date AS DueDate, amount, paid, balance, status";

        public async Task<APBill?> GetApBillById(long id, IDbTransaction? tx)
        {
            if (tx is not null)
            {
                // FOR UPDATE: a second payment on the same bill waits until this transaction ends.
                return await tx.Connection!.QuerySingleOrDefaultAsync<APBill>(
                    $"SELECT {BillColumns} FROM ap_bill WHERE id = @Id FOR UPDATE",
                    new { Id = id }, tx);
            }

            using IDbConnection connection = _context.GetDatabaseConnection();
            return await connection.QuerySingleOrDefaultAsync<APBill>(
                $"SELECT {BillColumns} FROM ap_bill WHERE id = @Id",
                new { Id = id });
        }

        // Paid, balance and status change together; amount and vehicle_id are never touched.
        public async Task<int> UpdateApBillPayment(APBill bill, IDbTransaction tx)
        {
            return await tx.Connection!.ExecuteAsync(
                "UPDATE ap_bill SET paid = @Paid, balance = @Balance, status = @Status WHERE id = @Id",
                new { bill.Id, bill.Paid, bill.Balance, bill.Status },
                tx);
        }

        public async Task<decimal> GetOpenBalanceByVendor(long vendorId, IDbTransaction tx)
        {
            return await tx.Connection!.ExecuteScalarAsync<decimal>(
                "SELECT COALESCE(SUM(balance), 0) FROM ap_bill WHERE vendor_id = @VendorId AND balance > 0",
                new { VendorId = vendorId },
                tx);
        }
    }
}
