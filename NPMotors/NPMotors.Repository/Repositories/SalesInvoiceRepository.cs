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
using System.Linq;
using NPMotors.Repository.RequestFeatures;

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
            public decimal? SellingPrice { get; set; }
        }

        // The sale price is never taken from the request: it is the vehicle's selling_price.
        private static async Task ValidateSelectionAsync(IDbConnection connection, SalesInvoiceCreateDto invoice, IDbTransaction? transaction = null)
        {
            var selection = await connection.QuerySingleAsync<SelectionStatus>(
                @"SELECT (SELECT LOWER(status) FROM customer WHERE id = @CustomerId) AS CustomerStatus,
                         (SELECT LOWER(status) FROM employee WHERE id = @SalespersonId) AS SalespersonStatus,
                         (SELECT COALESCE(commission, 0) > 0 FROM employee WHERE id = @SalespersonId) AS IsSalesperson,
                         (SELECT LOWER(status) FROM vehicle WHERE id = @VehicleId) AS VehicleStatus,
                         (SELECT selling_price FROM vehicle WHERE id = @VehicleId) AS SellingPrice",
                new { CustomerId = invoice.Customer_Id, SalespersonId = invoice.Salesperson_Id, VehicleId = invoice.Vehicle_Id },
                transaction);

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
            if (selection.SellingPrice is null || selection.SellingPrice < 0)
                throw new ArgumentException("The vehicle has no selling price.");
            if (invoice.Discount > selection.SellingPrice)
                throw new ArgumentException("Discount cannot exceed the sale price.");
            if (selection.SellingPrice + invoice.Tax + invoice.Fees - invoice.Discount <= 0)
                throw new ArgumentException("Invoice total must be greater than zero.");
        }

        public async Task<SalesInvoiceResponseDto> AddSalesInvoice(SalesInvoiceCreateDto invoice)
        {
            using IDbConnection connection = _context.GetDatabaseConnection();

            await ValidateSelectionAsync(connection, invoice);

            var queryParams = new DynamicParameters();
            queryParams.Add("CustomerId", invoice.Customer_Id);
            queryParams.Add("SalespersonId", invoice.Salesperson_Id);
            queryParams.Add("VehicleId", invoice.Vehicle_Id);
            queryParams.Add("InvoiceDate", invoice.Invoice_Date.Date, DbType.Date);
            queryParams.Add("DueDate", invoice.Due_Date?.Date, DbType.Date);
                        queryParams.Add("Tax", invoice.Tax);
            queryParams.Add("Fees", invoice.Fees);
            queryParams.Add("Discount", invoice.Discount);
            queryParams.Add("ActiveStatus", Status.Active.ToDbValue());
            queryParams.Add("VehicleStatus", VehicleStatus.Available.ToDbValue());
            queryParams.Add("Lifecycle", InvoiceLifecycle.Draft.ToDbValue());

            // The number comes from the id's own sequence ('INV-000001'), so it is unique per invoice.
            // The WHERE re-checks customer/salesperson/vehicle in the same statement as the INSERT.
            // A Draft starts with paid = 0, balance = total and no payment status; the vehicle row is not updated.
            var created = await QueryInvoiceAsync(connection,
                @"WITH new_id AS (SELECT nextval(pg_get_serial_sequence('sales_invoice', 'id')) AS id),
                       ins AS (
                           INSERT INTO sales_invoice (id, number, date, customer_id, salesperson_id, vehicle_id, sale_price,
                                                      tax, fees, discount, total, paid, balance, due_date, lifecycle)
                           SELECT n.id, 'INV-' || LPAD(n.id::text, 6, '0'), @InvoiceDate, c.id, e.id, v.id, v.selling_price,
                                  @Tax, @Fees, @Discount, v.selling_price + @Tax + @Fees - @Discount, 0,
                                  v.selling_price + @Tax + @Fees - @Discount, @DueDate, @Lifecycle
                           FROM new_id n, customer c, employee e, vehicle v
                           WHERE c.id = @CustomerId AND LOWER(c.status) = @ActiveStatus
                             AND e.id = @SalespersonId AND LOWER(e.status) = @ActiveStatus AND COALESCE(e.commission, 0) > 0
                             AND v.id = @VehicleId AND LOWER(v.status) = @VehicleStatus
                           RETURNING id, number, date, due_date, customer_id, salesperson_id, vehicle_id, sale_price,
                                     tax, fees, discount, paid, lifecycle, payment_status
                       )
                   SELECT ins.id, ins.number, ins.date AS Invoice_Date, ins.due_date AS Due_Date,
                          ins.customer_id AS Customer_Id, ins.salesperson_id AS Salesperson_Id, ins.vehicle_id AS Vehicle_Id,
                          v.vin, v.make, v.model, COALESCE(v.selling_price, 0) AS Selling_Price,
                          COALESCE(v.total_cost, 0) AS Vehicle_Total_Cost, ins.sale_price AS Sale_Price,
                          ins.tax, ins.fees, ins.discount, ins.paid, ins.lifecycle AS Lifecycle, ins.payment_status AS Payment_Status
                   FROM ins JOIN vehicle v ON v.id = ins.vehicle_id",
                queryParams);

            return created ?? throw new ArgumentException("The invoice could not be created because the customer, salesperson or vehicle is no longer eligible.");
        }

        public async Task<SalesInvoiceResponseDto> PostSalesInvoice(SalesInvoiceCreateDto invoice)
        {
            using IDbConnection connection = _context.GetDatabaseConnection();
            if (connection.State != ConnectionState.Open)
                connection.Open();
            using IDbTransaction transaction = connection.BeginTransaction();

            await ValidateSelectionAsync(connection, invoice, transaction);

            var queryParams = new DynamicParameters();
            queryParams.Add("CustomerId", invoice.Customer_Id);
            queryParams.Add("SalespersonId", invoice.Salesperson_Id);
            queryParams.Add("VehicleId", invoice.Vehicle_Id);
            queryParams.Add("InvoiceDate", invoice.Invoice_Date.Date, DbType.Date);
            queryParams.Add("DueDate", invoice.Due_Date?.Date, DbType.Date);
                        queryParams.Add("Tax", invoice.Tax);
            queryParams.Add("Fees", invoice.Fees);
            queryParams.Add("Discount", invoice.Discount);
            queryParams.Add("ActiveStatus", Status.Active.ToDbValue());
            queryParams.Add("VehicleStatus", VehicleStatus.Available.ToDbValue());
            queryParams.Add("Lifecycle", InvoiceLifecycle.Posted.ToDbValue());
            queryParams.Add("PaymentStatus", PaymentStatus.Unpaid.ToDbValue());

            // Validate again and insert in one statement; the vehicle row is locked so a concurrent post waits.
            // A Posted invoice starts Unpaid with paid = 0 and balance = total.
            SalesInvoiceResponseDto? created;
            try
            {
                created = await connection.QuerySingleOrDefaultAsync<SalesInvoiceResponseDto>(
                    @"WITH new_id AS (SELECT nextval(pg_get_serial_sequence('sales_invoice', 'id')) AS id),
                           ins AS (
                               INSERT INTO sales_invoice (id, number, date, customer_id, salesperson_id, vehicle_id, sale_price,
                                                          tax, fees, discount, total, paid, balance, due_date, lifecycle, payment_status)
                               SELECT n.id, 'INV-' || LPAD(n.id::text, 6, '0'), @InvoiceDate, c.id, e.id, v.id, v.selling_price,
                                      @Tax, @Fees, @Discount, v.selling_price + @Tax + @Fees - @Discount, 0,
                                      v.selling_price + @Tax + @Fees - @Discount, @DueDate, @Lifecycle, @PaymentStatus
                               FROM new_id n, customer c, employee e, vehicle v
                               WHERE c.id = @CustomerId AND LOWER(c.status) = @ActiveStatus
                                 AND e.id = @SalespersonId AND LOWER(e.status) = @ActiveStatus AND COALESCE(e.commission, 0) > 0
                                 AND v.id = @VehicleId AND LOWER(v.status) = @VehicleStatus
                               RETURNING id, number, date, due_date, customer_id, salesperson_id, vehicle_id, sale_price,
                                         tax, fees, discount, paid, lifecycle, payment_status
                           )
                       SELECT ins.id, ins.number, ins.date AS Invoice_Date, ins.due_date AS Due_Date,
                              ins.customer_id AS Customer_Id, ins.salesperson_id AS Salesperson_Id, ins.vehicle_id AS Vehicle_Id,
                              v.vin, v.make, v.model, COALESCE(v.selling_price, 0) AS Selling_Price,
                              COALESCE(v.total_cost, 0) AS Vehicle_Total_Cost, ins.sale_price AS Sale_Price,
                              ins.tax, ins.fees, ins.discount, ins.paid, ins.lifecycle AS Lifecycle, ins.payment_status AS Payment_Status
                       FROM ins JOIN vehicle v ON v.id = ins.vehicle_id",
                    queryParams, transaction);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                transaction.Rollback();
                throw new ArgumentException("This vehicle already has a sales invoice.");
            }

            if (created is null)
            {
                transaction.Rollback();
                throw new ArgumentException("The invoice could not be posted because the customer, salesperson or vehicle is not eligible.");
            }

            // Mark the vehicle Sold and link it to the invoice; 0 rows means it was sold in the meantime.
            var vehicleRows = await connection.ExecuteAsync(
                @"UPDATE vehicle SET status = @Sold, invoice_id = @Id
                  WHERE id = @VehicleId AND LOWER(status) = @Available",
                new { Id = created.Id, VehicleId = invoice.Vehicle_Id, Sold = VehicleStatus.Sold.ToDbValue(), Available = VehicleStatus.Available.ToDbValue() },
                transaction);
            if (vehicleRows == 0)
            {
                transaction.Rollback();
                throw new ArgumentException("The vehicle is no longer available (already sold).");
            }

            transaction.Commit();
            return created;
        }

        // Stored columns only; Total and Balance are filled by SalesInvoiceService.
        private const string BrowseColumns =
            @"si.id, si.number, si.date AS Invoice_Date, si.due_date AS Due_Date,
              si.customer_id AS Customer_Id, CONCAT_WS(' ', c.first_name, c.last_name) AS Customer_Name,
              si.salesperson_id AS Salesperson_Id, si.vehicle_id AS Vehicle_Id,
              v.vin, v.make, v.model, v.status AS Vehicle_Status,
              COALESCE(v.selling_price, 0) AS Selling_Price, COALESCE(v.total_cost, 0) AS Vehicle_Total_Cost,
              COALESCE(si.sale_price, 0) AS Sale_Price, si.tax, si.fees, si.discount, si.paid,
              si.lifecycle AS Lifecycle, si.payment_status AS Payment_Status, si.cancel_reason AS Cancel_Reason";

        private const string BrowseFrom =
            "FROM sales_invoice si JOIN customer c ON c.id = si.customer_id JOIN vehicle v ON v.id = si.vehicle_id";

        // Cancelled invoices are included unless the Lifecycle filter says otherwise.
        public async Task<PagedList<SalesInvoiceResponseDto>> GetAllSalesInvoices(SalesInvoiceParameters parameters)
        {
            using IDbConnection connection = _context.GetDatabaseConnection();

            var whereClauses = new List<string>();
            var queryParams = new DynamicParameters();

            var lifecycle = parameters.Lifecycle?.Trim();
            if (!string.IsNullOrWhiteSpace(lifecycle))
            {
                whereClauses.Add("LOWER(si.lifecycle) = @Lifecycle");
                queryParams.Add("Lifecycle", lifecycle.ToLowerInvariant());
            }

            var status = parameters.Status?.Trim();
            if (!string.IsNullOrWhiteSpace(status))
            {
                whereClauses.Add("LOWER(si.payment_status) = @Status");
                queryParams.Add("Status", status.ToLowerInvariant());
            }

            var searchTerm = parameters.SearchTerm?.Trim();
            if (!string.IsNullOrWhiteSpace(searchTerm))
            {
                whereClauses.Add(@"(
                    LOWER(si.number) LIKE @SearchTerm OR
                    LOWER(CONCAT_WS(' ', c.first_name, c.last_name)) LIKE @SearchTerm OR
                    LOWER(v.vin) LIKE @SearchTerm
                )");
                queryParams.Add("SearchTerm", $"%{searchTerm.ToLowerInvariant()}%");
            }

            if (parameters.FromDate is not null)
            {
                whereClauses.Add("si.date >= @FromDate");
                queryParams.Add("FromDate", parameters.FromDate.Value.Date, DbType.Date);
            }

            if (parameters.ToDate is not null)
            {
                whereClauses.Add("si.date <= @ToDate");
                queryParams.Add("ToDate", parameters.ToDate.Value.Date, DbType.Date);
            }

            var whereSql = whereClauses.Count > 0
                ? " WHERE " + string.Join(" AND ", whereClauses)
                : string.Empty;

            var count = await connection.ExecuteScalarAsync<int>($"SELECT COUNT(*) {BrowseFrom}{whereSql}", queryParams);

            queryParams.Add("Offset", (parameters.PageNumber - 1) * parameters.PageSize);
            queryParams.Add("Limit", parameters.PageSize);

            var invoices = (await connection.QueryAsync<SalesInvoiceResponseDto>(
                $"SELECT {BrowseColumns} {BrowseFrom}{whereSql} ORDER BY si.date DESC, si.id DESC OFFSET @Offset LIMIT @Limit",
                queryParams)).ToList();

            return new PagedList<SalesInvoiceResponseDto>(invoices, count, parameters.PageNumber, parameters.PageSize);
        }

        public async Task<SalesInvoiceResponseDto?> GetSalesInvoiceById(long id)
        {
            using IDbConnection connection = _context.GetDatabaseConnection();

            return await connection.QuerySingleOrDefaultAsync<SalesInvoiceResponseDto>(
                $"SELECT {BrowseColumns} {BrowseFrom} WHERE si.id = @Id",
                new { Id = id });
        }

        private sealed class CancelState
        {
            public string? Lifecycle { get; set; }
            public decimal Paid { get; set; }
        }

        // One transaction: lock and check the invoice, mark it Cancelled with the reason, return the vehicle to inventory.
        // A missing invoice throws KeyNotFoundException (404); a rule violation throws ArgumentException (400).
        public async Task<SalesInvoiceResponseDto> CancelSalesInvoice(long id, SalesInvoiceCancelDto reason)
        {
            using IDbConnection connection = _context.GetDatabaseConnection();
            if (connection.State != ConnectionState.Open)
                connection.Open();
            using IDbTransaction transaction = connection.BeginTransaction();

            var current = await connection.QuerySingleOrDefaultAsync<CancelState>(
                "SELECT LOWER(lifecycle) AS Lifecycle, paid AS Paid FROM sales_invoice WHERE id = @Id FOR UPDATE",
                new { Id = id }, transaction);

            if (current is null)
            {
                transaction.Rollback();
                throw new KeyNotFoundException("The invoice does not exist.");
            }
            if (current.Lifecycle != InvoiceLifecycle.Posted.ToDbValue())
            {
                transaction.Rollback();
                throw new ArgumentException("Only Posted invoices can be cancelled.");
            }
            if (current.Paid != 0)
            {
                transaction.Rollback();
                throw new ArgumentException("An invoice with payments cannot be cancelled.");
            }

            // The invoice keeps its vehicle_id as history; only the vehicle is released (matched through invoice_id).
            await connection.ExecuteAsync(
                "UPDATE vehicle SET status = @Available, invoice_id = NULL WHERE invoice_id = @Id",
                new { Id = id, Available = VehicleStatus.Available.ToDbValue() },
                transaction);

            await connection.ExecuteAsync(
                "UPDATE sales_invoice SET lifecycle = @Cancelled, cancel_reason = @Reason WHERE id = @Id",
                new { Id = id, Cancelled = InvoiceLifecycle.Cancelled.ToDbValue(), Reason = reason.Reason },
                transaction);

            var cancelled = await connection.QuerySingleAsync<SalesInvoiceResponseDto>(
                $"SELECT {BrowseColumns} {BrowseFrom} WHERE si.id = @Id",
                new { Id = id }, transaction);

            transaction.Commit();
            return cancelled;
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
