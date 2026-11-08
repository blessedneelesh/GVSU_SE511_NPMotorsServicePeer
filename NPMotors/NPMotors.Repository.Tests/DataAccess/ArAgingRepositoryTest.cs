using System.Data;
using Dapper;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.Models.Enums;
using NPMotors.Repository.Repositories;
using NPMotors.Repository.RequestFeatures;

namespace NPMotors.Repository.Tests.DataAccess
{
    // Story 17: ArInvoiceRepository.GetArAging. As-of date: 2026-11-08.
    // Customer Alpha (A) rows:                     due         amount paid  balance status
    //   A1  past due                               2026-10-01  1000   0     1000    unpaid   -> Overdue
    //   A2  past due, partly paid                  2026-10-15  1000   400   600     partial  -> Overdue
    //   A3  due later                              2026-12-01  1000   0     1000    unpaid
    //   A4  due on the as-of date                  2026-11-08  1000   300   700     partial
    //   A5  no due date                            NULL        1000   0     1000    unpaid
    //   A6  fully paid (balance 0)                 2026-09-01  1000   1000  0       paid     -> never listed
    //   A7  due later, same date as A3, lower no.  2026-12-01  1000   0     1000    unpaid
    // Customer Beta (B) row:
    //   B1  past due                               2026-10-20  500    0     500     unpaid   -> Overdue
    public class ArAgingRepositoryTest : TestBase, IAsyncLifetime
    {
        private static readonly DateTime AsOf = new(2026, 11, 8);

        private readonly ArInvoiceRepository _repository;
        private readonly string _marker = $"itest-{Guid.NewGuid():N}".Substring(0, 12);
        private long _alphaId;
        private long _betaId;
        private int _vehicleCount;
        private readonly Dictionary<string, string> _invoiceNumbers = new();

        public ArAgingRepositoryTest()
        {
            _repository = new ArInvoiceRepository(_repositoryContext);
        }

        public async Task InitializeAsync()
        {
            _alphaId = await AddCustomerAsync("Alpha");
            _betaId = await AddCustomerAsync("Beta");
            var salespersonId = (await new EmployeeRepository(_repositoryContext).AddEmployee(new EmployeeCreateDto
            {
                First_Name = _marker, Last_Name = "Seller", Department = "Sales", Commission_Rate = 5m, Status = Status.Active
            })).Id;
            var vendor = await new VendorRepository(_repositoryContext).AddVendor(new VendorCreateDto
            {
                Name = $"{_marker} Vendor", Type = VendorType.Vehicles, Status = Status.Active
            });

            await AddArAsync("A1", _alphaId, salespersonId, vendor.Id, new DateTime(2026, 10, 1), 1000m, 0m, "unpaid");
            await AddArAsync("A2", _alphaId, salespersonId, vendor.Id, new DateTime(2026, 10, 15), 1000m, 400m, "partial");
            await AddArAsync("A3", _alphaId, salespersonId, vendor.Id, new DateTime(2026, 12, 1), 1000m, 0m, "unpaid");
            await AddArAsync("A4", _alphaId, salespersonId, vendor.Id, new DateTime(2026, 11, 8), 1000m, 300m, "partial");
            await AddArAsync("A5", _alphaId, salespersonId, vendor.Id, null, 1000m, 0m, "unpaid");
            await AddArAsync("A6", _alphaId, salespersonId, vendor.Id, new DateTime(2026, 9, 1), 1000m, 1000m, "paid");
            await AddArAsync("A0", _alphaId, salespersonId, vendor.Id, new DateTime(2026, 12, 1), 1000m, 0m, "unpaid");
            await AddArAsync("B1", _betaId, salespersonId, vendor.Id, new DateTime(2026, 10, 20), 500m, 0m, "unpaid");
        }

        public async Task DisposeAsync()
        {
            using IDbConnection connection = _repositoryContext.GetDatabaseConnection();
            var pattern = new { Pattern = _marker + "%", VinPattern = _marker.ToUpperInvariant() + "%" };
            const string markerVehicles =
                "SELECT id FROM vehicle WHERE vendor_id IN (SELECT id FROM vendor WHERE name LIKE @Pattern) OR vin LIKE @VinPattern";
            const string markerInvoices =
                "SELECT id FROM sales_invoice WHERE vehicle_id IN (" + markerVehicles + ") OR customer_id IN (SELECT id FROM customer WHERE first_name LIKE @Pattern)";
            await connection.ExecuteAsync($"DELETE FROM ar_invoice WHERE invoice_id IN ({markerInvoices})", pattern);
            await connection.ExecuteAsync($"UPDATE vehicle SET invoice_id = NULL WHERE id IN ({markerVehicles})", pattern);
            await connection.ExecuteAsync($"DELETE FROM sales_invoice WHERE id IN ({markerInvoices})", pattern);
            await connection.ExecuteAsync($"DELETE FROM vehicle WHERE id IN ({markerVehicles})", pattern);
            await connection.ExecuteAsync("DELETE FROM vendor WHERE name LIKE @Pattern", pattern);
            await connection.ExecuteAsync("DELETE FROM customer WHERE first_name LIKE @Pattern", pattern);
            await connection.ExecuteAsync("DELETE FROM employee WHERE first_name LIKE @Pattern", pattern);
        }

        private async Task<long> AddCustomerAsync(string lastName) =>
            (await new CustomerRepository(_repositoryContext).AddCustomer(new CustomerCreateDto
            {
                First_Name = _marker, Last_Name = lastName, Email = $"{_marker}.{lastName}@example.com", Status = Status.Active
            })).Id;

        // A draft sales invoice (for the FK) plus an ar_invoice row inserted straight into the table.
        private async Task AddArAsync(string key, long customerId, long salespersonId, long vendorId,
            DateTime? due, decimal amount, decimal paid, string status)
        {
            var vehicle = await new VehicleRepository(_repositoryContext).AddVehicle(new VehicleCreateDto
            {
                Vendor_Id = vendorId,
                Vin = $"{_marker}{++_vehicleCount}".ToUpperInvariant().PadRight(17, '0').Substring(0, 17),
                Make = "Honda", Model = "Civic",
                Purchase_Price = 15000m, Additional_Costs = 250m, Selling_Price = 18000m
            });
            var invoice = await new SalesInvoiceRepository(_repositoryContext).AddSalesInvoice(new SalesInvoiceCreateDto
            {
                Customer_Id = customerId, Salesperson_Id = salespersonId, Vehicle_Id = vehicle.Id,
                Invoice_Date = new DateTime(2026, 5, 1)
            });
            _invoiceNumbers[key] = invoice.Number;

            using IDbConnection connection = _repositoryContext.GetDatabaseConnection();
            await connection.ExecuteAsync(
                @"INSERT INTO ar_invoice (invoice_id, number, date, due_date, amount, paid, balance, status, created_at, created_by)
                  VALUES (@InvoiceId, @Number, @Date, @Due, @Amount, @Paid, @Balance, @Status, NOW(), 'system')",
                new
                {
                    InvoiceId = invoice.Id, Number = $"{_marker}-{key}", Date = new DateTime(2026, 5, 1), Due = due,
                    Amount = amount, Paid = paid, Balance = amount - paid, Status = status
                });
        }

        private string Number(string key) => $"{_marker}-{key}";

        private Task<PagedList<ArAgingDto>> GetAsync(long? customerId, string? status = null, int pageNumber = 1, int pageSize = 100, DateTime? asOf = null) =>
            _repository.GetArAging(new ArAgingParameters
            {
                AsOfDate = asOf ?? AsOf, CustomerId = customerId, Status = status, PageNumber = pageNumber, PageSize = pageSize
            });

        private static string[] Keys(PagedList<ArAgingDto> page, string marker) =>
            page.Select(i => i.Number.Substring(marker.Length + 1)).ToArray();

        // ---- Open AR only ----

        [Fact]
        public async Task GetArAging_ListsOnlyOpenBalances_PaidArIsLeftOut()
        {
            var page = await GetAsync(_alphaId);

            Assert.DoesNotContain(page, i => i.Number == Number("A6"));
            Assert.All(page, i => Assert.True(i.Balance > 0));
            Assert.Equal(6, page.Count);
        }

        // ---- Customer filter (AC 4) ----

        [Fact]
        public async Task GetArAging_CustomerFilter_ReturnsOnlyThatCustomer()
        {
            var page = await GetAsync(_betaId);

            var item = Assert.Single(page);
            Assert.Equal(Number("B1"), item.Number);
            Assert.Equal(_betaId, item.CustomerId);
        }

        [Fact]
        public async Task GetArAging_NoCustomer_ReturnsRowsOfEveryCustomer()
        {
            var page = await GetAsync(null);

            Assert.Contains(page, i => i.CustomerId == _alphaId);
            Assert.Contains(page, i => i.CustomerId == _betaId);
        }

        [Fact]
        public async Task GetArAging_UnknownCustomer_ReturnsEmptyPagedList()
        {
            var page = await GetAsync(long.MaxValue);

            Assert.NotNull(page);
            Assert.Empty(page);
            Assert.Equal(0, page.MetaData.TotalCount);
        }

        // ---- Status filter (AC 3, 4) ----

        [Fact]
        public async Task GetArAging_Overdue_ReturnsOpenRowsPastDue()
        {
            var page = await GetAsync(_alphaId, "Overdue");

            Assert.Equal(new[] { "A1", "A2" }, Keys(page, _marker));
        }

        [Fact]
        public async Task GetArAging_Overdue_IncludesPartialRowsPastDue()
        {
            var page = await GetAsync(_alphaId, "Overdue");

            Assert.Contains(page, i => i.Number == Number("A2") && i.Status == "partial");
        }

        [Fact]
        public async Task GetArAging_Overdue_RowDueOnAsOfDateIsNotOverdue()
        {
            var page = await GetAsync(_alphaId, "Overdue");

            Assert.DoesNotContain(page, i => i.Number == Number("A4"));
        }

        [Fact]
        public async Task GetArAging_Overdue_RowWithoutDueDateIsNotOverdue()
        {
            var page = await GetAsync(_alphaId, "Overdue");

            Assert.DoesNotContain(page, i => i.Number == Number("A5"));
        }

        [Fact]
        public async Task GetArAging_Unpaid_ReturnsUnpaidRowsNotPastDue()
        {
            var page = await GetAsync(_alphaId, "Unpaid");

            Assert.Equal(new[] { "A0", "A3", "A5" }, Keys(page, _marker).OrderBy(k => k).ToArray());
            Assert.All(page, i => Assert.Equal("unpaid", i.Status));
        }

        [Fact]
        public async Task GetArAging_Unpaid_PastDueUnpaidRowIsLeftOutBecauseItIsOverdue()
        {
            var page = await GetAsync(_alphaId, "Unpaid");

            Assert.DoesNotContain(page, i => i.Number == Number("A1"));
        }

        [Fact]
        public async Task GetArAging_Partial_ReturnsPartialRowsNotPastDue()
        {
            var page = await GetAsync(_alphaId, "Partial");

            var item = Assert.Single(page);
            Assert.Equal(Number("A4"), item.Number);
            Assert.Equal("partial", item.Status);
        }

        [Theory]
        [InlineData("overdue")]
        [InlineData("OVERDUE")]
        [InlineData("  Overdue  ")]
        public async Task GetArAging_StatusIsCaseAndWhitespaceInsensitive(string status)
        {
            var page = await GetAsync(_alphaId, status);

            Assert.Equal(new[] { "A1", "A2" }, Keys(page, _marker));
        }

        [Fact]
        public async Task GetArAging_LowerCaseUnpaid_Works()
        {
            var page = await GetAsync(_alphaId, "unpaid");

            Assert.Equal(3, page.Count);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("paid")]
        [InlineData("bogus")]
        public async Task GetArAging_EmptyOrInvalidStatus_IsIgnored(string status)
        {
            var page = await GetAsync(_alphaId, status);

            Assert.Equal(6, page.Count);
        }

        [Fact]
        public async Task GetArAging_NullStatus_ReturnsAllOpenRows()
        {
            var page = await GetAsync(_alphaId, null);

            Assert.Equal(6, page.Count);
        }

        [Fact]
        public async Task GetArAging_StatusOnly_EveryRowMatches()
        {
            var page = await GetAsync(null, "Overdue");

            Assert.Contains(page, i => i.Number == Number("A1"));
            Assert.Contains(page, i => i.Number == Number("B1"));
            Assert.All(page, i =>
            {
                Assert.NotNull(i.Due_Date);
                Assert.True(i.Due_Date < AsOf);
                Assert.True(i.Balance > 0);
            });
        }

        [Fact]
        public async Task GetArAging_StatusAndCustomer_BothApply()
        {
            var alpha = await GetAsync(_alphaId, "Overdue");
            var beta = await GetAsync(_betaId, "Overdue");
            var betaPartial = await GetAsync(_betaId, "Partial");

            Assert.Equal(2, alpha.Count);
            Assert.All(alpha, i => Assert.Equal(_alphaId, i.CustomerId));
            Assert.Equal(Number("B1"), Assert.Single(beta).Number);
            Assert.Empty(betaPartial);
        }

        // ---- AsOfDate ----

        [Fact]
        public async Task GetArAging_EarlierAsOfDate_FewerOverdueRows()
        {
            // At 2026-10-10 only A1 (due 2026-10-01) is past due.
            var page = await GetAsync(_alphaId, "Overdue", asOf: new DateTime(2026, 10, 10));

            Assert.Equal(new[] { "A1" }, Keys(page, _marker));
        }

        [Fact]
        public async Task GetArAging_LaterAsOfDate_MoreOverdueRows()
        {
            // At 2026-12-02 every Alpha row with a due date is past due (A1, A2, A4, A3, A0).
            var page = await GetAsync(_alphaId, "Overdue", asOf: new DateTime(2026, 12, 2));

            Assert.Equal(5, page.Count);
            Assert.DoesNotContain(page, i => i.Number == Number("A5"));
        }

        [Fact]
        public async Task GetArAging_NoAsOfDate_UsesToday()
        {
            var page = await _repository.GetArAging(new ArAgingParameters { CustomerId = _alphaId, Status = "Overdue", PageSize = 100 });

            // Overdue means due before today: A1, A2, A4 (2026-11-08), A3 and A0 (2026-12-01) once today is later.
            var expected = new[] { new DateTime(2026, 10, 1), new DateTime(2026, 10, 15), new DateTime(2026, 11, 8),
                                   new DateTime(2026, 12, 1), new DateTime(2026, 12, 1) }.Count(d => d < DateTime.Today);
            Assert.Equal(expected, page.Count);
        }

        // ---- Order, paging ----

        [Fact]
        public async Task GetArAging_OrderedByDueDateThenNumber_NullDueDateLast()
        {
            var page = await GetAsync(_alphaId);

            Assert.Equal(new[] { "A1", "A2", "A4", "A0", "A3", "A5" }, Keys(page, _marker));
        }

        [Fact]
        public async Task GetArAging_Paging_SecondPageAndTotalCount()
        {
            var page = await GetAsync(_alphaId, pageNumber: 2, pageSize: 2);

            Assert.Equal(new[] { "A4", "A0" }, Keys(page, _marker));
            Assert.Equal(6, page.MetaData.TotalCount);
            Assert.Equal(2, page.MetaData.CurrentPage);
            Assert.Equal(2, page.MetaData.PageSize);
            Assert.Equal(3, page.MetaData.TotalPages);
        }

        [Fact]
        public async Task GetArAging_PagePastTheEnd_ReturnsEmptyListWithTotalCount()
        {
            var page = await GetAsync(_alphaId, pageNumber: 5, pageSize: 2);

            Assert.Empty(page);
            Assert.Equal(6, page.MetaData.TotalCount);
        }

        [Fact]
        public async Task GetArAging_PageSizeAboveTheCap_IsClamped()
        {
            var page = await GetAsync(_alphaId, pageSize: 1000);

            Assert.Equal(100, page.MetaData.PageSize);
        }

        [Fact]
        public async Task GetArAging_TotalCountRespectsTheFilters()
        {
            var page = await GetAsync(_alphaId, "Overdue", pageSize: 1);

            Assert.Single(page);
            Assert.Equal(2, page.MetaData.TotalCount);
        }

        // ---- Columns (AC 1) ----

        [Fact]
        public async Task GetArAging_FillsEveryStoredColumnFromTheJoins()
        {
            var page = await GetAsync(_alphaId, "Partial");

            var item = Assert.Single(page);
            Assert.True(item.Id > 0);
            Assert.Equal(Number("A4"), item.Number);
            Assert.Equal(_alphaId, item.CustomerId);
            Assert.Equal($"{_marker} Alpha", item.Customer_Name);
            Assert.Equal(_invoiceNumbers["A4"], item.Invoice_Number);
            Assert.Equal(new DateTime(2026, 11, 8), item.Due_Date);
            Assert.Equal(1000m, item.Amount);
            Assert.Equal(300m, item.Paid);
            Assert.Equal(700m, item.Balance);
            Assert.Equal("partial", item.Status);
        }

        [Fact]
        public async Task GetArAging_NoDueDate_DueDateIsNull()
        {
            var page = await GetAsync(_alphaId, "Unpaid");

            Assert.Null(page.Single(i => i.Number == Number("A5")).Due_Date);
        }

        [Fact]
        public async Task GetArAging_CalculatedFieldsAreLeftAtTheirDefaults()
        {
            var page = await GetAsync(_alphaId);

            Assert.All(page, i =>
            {
                Assert.Equal(0, i.Days_Overdue);
                Assert.Equal(string.Empty, i.Aging_Status);
            });
        }

        [Fact]
        public async Task GetArAging_DoesNotChangeTheStoredRows()
        {
            await GetAsync(_alphaId, "Overdue");

            using IDbConnection connection = _repositoryContext.GetDatabaseConnection();
            var status = await connection.ExecuteScalarAsync<string>(
                "SELECT status FROM ar_invoice WHERE number = @Number", new { Number = Number("A1") });
            Assert.Equal("unpaid", status);
        }
    }
}
