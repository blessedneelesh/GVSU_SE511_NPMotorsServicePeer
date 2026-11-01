using NPMotors.Repository.Interface;
using System;
using System.Collections.Generic;
using System.Data;
using System.Text;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.Models.Enums;
using System.Threading.Tasks;
using Dapper;
using Npgsql;

namespace NPMotors.Repository.Repositories
{
    public class SalesInvoiceRepository : ISalesInvoiceRepository
    {
        private readonly IRepositoryContext _context;

        public SalesInvoiceRepository(IRepositoryContext context)
        {
            _context = context;
        }

        private sealed class SelectionStatus
        {
            public string? CustomerStatus { get; set; }
            public string? SalespersonStatus { get; set; }
            public bool? IsSalesperson { get; set; }
            public string? VehicleStatus { get; set; }
        }

        public async Task<SalesInvoiceResponseDto> AddSalesInvoice(SalesInvoiceCreateDto invoice)
        {
            using IDbConnection connection = _context.GetDatabaseConnection();

            var selection = await connection.QuerySingleAsync<SelectionStatus>(
                @"SELECT (SELECT LOWER(status) FROM customer WHERE id = @CustomerId) AS CustomerStatus,
                         (SELECT LOWER(status) FROM employee WHERE id = @SalespersonId) AS SalespersonStatus,
                         (SELECT COALESCE(commission, 0) > 0 FROM employee WHERE id = @SalespersonId) AS IsSalesperson,
                         (SELECT LOWER(status) FROM vehicle WHERE id = @VehicleId) AS VehicleStatus",
                new { CustomerId = invoice.Customer_Id, SalespersonId = invoice.Salesperson_Id, VehicleId = invoice.Vehicle_Id });

            if (selection.CustomerStatus is null)
                throw new ArgumentException("Customer does not exist.");
            if (selection.CustomerStatus != Status.Active.ToDbValue())
                throw new ArgumentException("Only active customers can be selected.");
            if (selection.SalespersonStatus is null)
                throw new ArgumentException("Salesperson does not exist.");
            if (selection.SalespersonStatus != Status.Active.ToDbValue() || selection.IsSalesperson != true)
                throw new ArgumentException("Only active salespeople can be assigned.");
            if (selection.VehicleStatus is null)
                throw new ArgumentException("Vehicle does not exist.");
            if (selection.VehicleStatus != VehicleStatus.Available.ToDbValue())
                throw new ArgumentException("Only available vehicles can be selected.");

            var queryParams = new DynamicParameters();
            queryParams.Add("CustomerId", invoice.Customer_Id);
            queryParams.Add("SalespersonId", invoice.Salesperson_Id);
            queryParams.Add("VehicleId", invoice.Vehicle_Id);
            queryParams.Add("InvoiceDate", invoice.Invoice_Date.Date, DbType.Date);
            queryParams.Add("DueDate", invoice.Due_Date?.Date, DbType.Date);
            queryParams.Add("SalePrice", invoice.Sale_Price);
            queryParams.Add("Tax", invoice.Tax);
            queryParams.Add("Fees", invoice.Fees);
            queryParams.Add("Discount", invoice.Discount);
            queryParams.Add("ActiveStatus", Status.Active.ToDbValue());
            queryParams.Add("VehicleStatus", VehicleStatus.Available.ToDbValue());

            // The number comes from the id's own sequence ('INV-000001'), so it is unique per invoice.
            // The WHERE re-checks customer/salesperson/vehicle in the same statement as the INSERT.
            // A Draft starts with paid = 0 and balance = total; the vehicle row is not updated.
            var created = await QueryInvoiceAsync(connection,
                @"WITH new_id AS (SELECT nextval(pg_get_serial_sequence('sales_invoice', 'id')) AS id),
                       ins AS (
                           INSERT INTO sales_invoice (id, number, date, customer_id, salesperson_id, vehicle_id, sale_price,
                                                      tax, fees, discount, total, paid, balance, due_date, lifecycle)
                           SELECT n.id, 'INV-' || LPAD(n.id::text, 6, '0'), @InvoiceDate, c.id, e.id, v.id, @SalePrice,
                                  @Tax, @Fees, @Discount, @SalePrice + @Tax + @Fees - @Discount, 0,
                                  @SalePrice + @Tax + @Fees - @Discount, @DueDate, 'draft'
                           FROM new_id n, customer c, employee e, vehicle v
                           WHERE c.id = @CustomerId AND LOWER(c.status) = @ActiveStatus
                             AND e.id = @SalespersonId AND LOWER(e.status) = @ActiveStatus AND COALESCE(e.commission, 0) > 0
                             AND v.id = @VehicleId AND LOWER(v.status) = @VehicleStatus
                           RETURNING id, number, date, due_date, customer_id, salesperson_id, vehicle_id, sale_price,
                                     tax, fees, discount, paid, lifecycle
                       )
                   SELECT ins.id, ins.number, ins.date AS Invoice_Date, ins.due_date AS Due_Date,
                          ins.customer_id AS Customer_Id, ins.salesperson_id AS Salesperson_Id, ins.vehicle_id AS Vehicle_Id,
                          v.vin, v.make, v.model, COALESCE(v.selling_price, 0) AS Selling_Price,
                          COALESCE(v.total_cost, 0) AS Vehicle_Total_Cost, ins.sale_price AS Sale_Price,
                          ins.tax, ins.fees, ins.discount, ins.paid, INITCAP(ins.lifecycle) AS Status
                   FROM ins JOIN vehicle v ON v.id = ins.vehicle_id",
                queryParams);

            return created ?? throw new ArgumentException("The invoice could not be created because the customer, salesperson or vehicle is no longer eligible.");
        }

        private static async Task<SalesInvoiceResponseDto?> QueryInvoiceAsync(IDbConnection connection, string sql, DynamicParameters queryParams)
        {
            try
            {
                return await connection.QuerySingleOrDefaultAsync<SalesInvoiceResponseDto>(sql, queryParams);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                // uq_sales_invoice_vehicle: a vehicle can only be on one invoice.
                throw new ArgumentException("This vehicle already has a sales invoice.");
            }
        }
    }
}
