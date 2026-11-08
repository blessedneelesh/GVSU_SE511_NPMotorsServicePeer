using NPMotors.Repository.Interface;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.Models.Enums;
using NPMotors.Repository.RequestFeatures;
using NPMotors.Services.Service;

namespace Module.Tests.Services
{
    public class SalesInvoiceServiceTest
    {
        // Hand-written fake: the test projects do not use a mocking library.
        private class FakeSalesInvoiceRepository : ISalesInvoiceRepository
        {
            // Story 9: browse and cancel.
            public PagedList<SalesInvoiceResponseDto> Page { get; set; } = new(new List<SalesInvoiceResponseDto>(), 0, 1, 5);
            public SalesInvoiceResponseDto? ByIdResult { get; set; }
            public SalesInvoiceResponseDto? CancelResult { get; set; }
            public List<(long Id, SalesInvoiceCancelDto Reason)> CancelCalls { get; } = new();

            public Task<PagedList<SalesInvoiceResponseDto>> GetAllSalesInvoices(SalesInvoiceParameters parameters)
            {
                return Task.FromResult(Page);
            }

            public Task<SalesInvoiceResponseDto?> GetSalesInvoiceById(long id)
            {
                return Task.FromResult(ByIdResult);
            }

            // Story 14 members: not used by these tests (covered by implement-story-test).
            public Task<NPMotors.Repository.Models.SalesInvoice?> GetInvoiceById(long id) =>
                throw new NotImplementedException();

            public Task<int> UpdateInvoicePayment(NPMotors.Repository.Models.SalesInvoice invoice, System.Data.IDbTransaction tx) =>
                throw new NotImplementedException();

            public Task<int> UpdatePostedInvoice(NPMotors.Repository.Models.SalesInvoice invoice, System.Data.IDbTransaction tx) =>
                throw new NotImplementedException();

            public Task<SalesInvoiceResponseDto> CancelSalesInvoice(long id, SalesInvoiceCancelDto reason)
            {
                CancelCalls.Add((id, reason));
                // Like the real repository: stored columns only, Total/Balance left at their defaults.
                return Task.FromResult(CancelResult ?? new SalesInvoiceResponseDto
                {
                    Id = id,
                    Sale_Price = 20000m,
                    Tax = 1500m,
                    Fees = 300m,
                    Discount = 500m,
                    Paid = 0m,
                    Lifecycle = InvoiceLifecycle.Cancelled,
                    Cancel_Reason = reason.Reason
                });
            }

            public SalesInvoiceResponseDto? Result { get; set; }
            // Stands in for vehicle.selling_price, which the real repository uses as the sale price.
            public decimal SellingPrice { get; set; } = 20000m;
            public List<SalesInvoiceCreateDto> AddCalls { get; } = new();

            public List<SalesInvoiceCreateDto> PostCalls { get; } = new();

            public Task<SalesInvoiceResponseDto> PostSalesInvoice(SalesInvoiceCreateDto invoice)
            {
                PostCalls.Add(invoice);
                // Like the real repository: echoes the stored columns (sale price = vehicle selling price), Total/Balance left at defaults.
                return Task.FromResult(Result ?? new SalesInvoiceResponseDto
                {
                    Id = 1,
                    Number = "INV-000001",
                    Invoice_Date = invoice.Invoice_Date,
                    Due_Date = invoice.Due_Date,
                    Customer_Id = invoice.Customer_Id,
                    Salesperson_Id = invoice.Salesperson_Id,
                    Vehicle_Id = invoice.Vehicle_Id,
                    Sale_Price = SellingPrice,
                    Tax = invoice.Tax,
                    Fees = invoice.Fees,
                    Discount = invoice.Discount,
                    Paid = 0m,
                    Lifecycle = InvoiceLifecycle.Posted,
                    Payment_Status = PaymentStatus.Unpaid
                });
            }

            public Task<SalesInvoiceResponseDto> AddSalesInvoice(SalesInvoiceCreateDto invoice)
            {
                AddCalls.Add(invoice);

                // Like the real repository: echoes the stored columns and leaves Total/Balance at their defaults.
                return Task.FromResult(Result ?? new SalesInvoiceResponseDto
                {
                    Id = 1,
                    Number = "INV-000001",
                    Invoice_Date = invoice.Invoice_Date,
                    Due_Date = invoice.Due_Date,
                    Customer_Id = invoice.Customer_Id,
                    Salesperson_Id = invoice.Salesperson_Id,
                    Vehicle_Id = invoice.Vehicle_Id,
                    Sale_Price = SellingPrice,
                    Tax = invoice.Tax,
                    Fees = invoice.Fees,
                    Discount = invoice.Discount,
                    Paid = 0m,
                    Lifecycle = InvoiceLifecycle.Draft
                });
            }
        }

        private readonly FakeSalesInvoiceRepository _repository = new();
        private readonly SalesInvoiceService _service;

        public SalesInvoiceServiceTest()
        {
            // Only the sales invoice repository is used by the Story 10-13 methods under test here.
            _service = new SalesInvoiceService(_repository, null!, null!, null!, null!, null!, null!);
        }

        private SalesInvoiceCreateDto NewInvoice(
            decimal salePrice = 20000m, decimal tax = 0m, decimal fees = 0m, decimal discount = 0m)
        {
            _repository.SellingPrice = salePrice;
            return new SalesInvoiceCreateDto
            {
                Customer_Id = 1,
                Salesperson_Id = 2,
                Vehicle_Id = 3,
                Invoice_Date = new DateTime(2026, 5, 1),
                Due_Date = new DateTime(2026, 5, 31),
                Tax = tax,
                Fees = fees,
                Discount = discount
            };
        }

        private async Task AssertRejectedAsync(SalesInvoiceCreateDto invoice, string expectedMessage)
        {
            var ex = await Assert.ThrowsAsync<ArgumentException>(() => _service.AddSalesInvoice(invoice));

            Assert.Equal(expectedMessage, ex.Message);
            Assert.Empty(_repository.AddCalls);
        }

        // ---- CalculateTotal via AddSalesInvoice ----

        [Fact]
        public async Task AddSalesInvoice_CalculatesTotal_SalePricePlusTaxPlusFeesMinusDiscount()
        {
            var result = await _service.AddSalesInvoice(NewInvoice(20000m, 1500m, 300m, 500m));

            Assert.Equal(21300m, result.Total);
        }

        [Fact]
        public async Task AddSalesInvoice_TotalKeepsCents()
        {
            var result = await _service.AddSalesInvoice(NewInvoice(19999.99m, 1200.50m, 99.25m, 0.74m));

            Assert.Equal(21299.00m, result.Total);
        }

        // ---- CalculateBalance via AddSalesInvoice ----

        [Fact]
        public async Task AddSalesInvoice_BalanceSubtractsPaid()
        {
            _repository.Result = new SalesInvoiceResponseDto
            {
                Id = 1,
                Number = "INV-000001",
                Sale_Price = 20000m,
                Tax = 1500m,
                Fees = 300m,
                Discount = 500m,
                Paid = 5000m,
                Lifecycle = InvoiceLifecycle.Draft
            };

            var result = await _service.AddSalesInvoice(NewInvoice(20000m, 1500m, 300m, 500m));

            Assert.Equal(21300m, result.Total);
            Assert.Equal(16300m, result.Balance);
        }

        // ---- ValidateInvoice via AddSalesInvoice ----

        [Fact]
        public async Task AddSalesInvoice_NegativeTax_Throws()
        {
            await AssertRejectedAsync(NewInvoice(tax: -1m), "Tax cannot be negative.");
        }

        [Fact]
        public async Task AddSalesInvoice_NegativeFees_Throws()
        {
            await AssertRejectedAsync(NewInvoice(fees: -1m), "Fees cannot be negative.");
        }

        [Fact]
        public async Task AddSalesInvoice_NegativeDiscount_Throws()
        {
            await AssertRejectedAsync(NewInvoice(discount: -1m), "Discount cannot be negative.");
        }

        [Theory]
        [InlineData(0)]
        public async Task AddSalesInvoice_MissingCustomer_Throws(long customerId)
        {
            var invoice = NewInvoice();
            invoice.Customer_Id = customerId;

            await AssertRejectedAsync(invoice, "Customer is required.");
        }

        [Theory]
        [InlineData(0)]
        public async Task AddSalesInvoice_MissingSalesperson_Throws(long salespersonId)
        {
            var invoice = NewInvoice();
            invoice.Salesperson_Id = salespersonId;

            await AssertRejectedAsync(invoice, "Salesperson is required.");
        }

        [Theory]
        [InlineData(0)]
        public async Task AddSalesInvoice_MissingVehicle_Throws(long vehicleId)
        {
            var invoice = NewInvoice();
            invoice.Vehicle_Id = vehicleId;

            await AssertRejectedAsync(invoice, "Vehicle is required.");
        }

        [Fact]
        public async Task AddSalesInvoice_MissingInvoiceDate_Throws()
        {
            var invoice = NewInvoice();
            invoice.Invoice_Date = default;

            await AssertRejectedAsync(invoice, "Invoice date is required.");
        }

        // ---- Public method behaviour ----

        [Fact]
        public async Task AddSalesInvoice_PassesInvoiceToRepositoryUnchanged()
        {
            var invoice = NewInvoice(20000m, 1500m, 300m, 500m);

            await _service.AddSalesInvoice(invoice);

            var call = Assert.Single(_repository.AddCalls);
            Assert.Same(invoice, call);
        }

        // ---- PostSalesInvoice ----

        [Fact]
        public async Task PostSalesInvoice_CalculatesTotal_SalePricePlusTaxPlusFeesMinusDiscount()
        {
            var result = await _service.PostSalesInvoice(NewInvoice(20000m, 1500m, 300m, 500m));

            Assert.Equal(21300m, result.Total);
        }

        [Fact]
        public async Task PostSalesInvoice_BalanceSubtractsPaid()
        {
            _repository.Result = new SalesInvoiceResponseDto
            {
                Id = 1,
                Sale_Price = 20000m,
                Tax = 1500m,
                Fees = 300m,
                Discount = 500m,
                Paid = 5000m,
                Lifecycle = InvoiceLifecycle.Posted
            };

            var result = await _service.PostSalesInvoice(NewInvoice());

            Assert.Equal(21300m, result.Total);
            Assert.Equal(16300m, result.Balance);
        }

        [Fact]
        public async Task PostSalesInvoice_PassesInvoiceToRepositoryUnchanged()
        {
            var invoice = NewInvoice(20000m, 1500m, 300m, 500m);

            await _service.PostSalesInvoice(invoice);

            var call = Assert.Single(_repository.PostCalls);
            Assert.Same(invoice, call);
            Assert.Empty(_repository.AddCalls);
        }

        private async Task AssertPostRejectedAsync(SalesInvoiceCreateDto invoice, string expectedMessage)
        {
            var ex = await Assert.ThrowsAsync<ArgumentException>(() => _service.PostSalesInvoice(invoice));

            Assert.Equal(expectedMessage, ex.Message);
            Assert.Empty(_repository.PostCalls);
        }

        [Theory]
        [InlineData(0)]
        public async Task PostSalesInvoice_MissingCustomer_Throws(long customerId)
        {
            var invoice = NewInvoice();
            invoice.Customer_Id = customerId;
            await AssertPostRejectedAsync(invoice, "Customer is required.");
        }

        // ---- Story 9: GetAllSalesInvoices (CalculateTotal / CalculateBalance applied to every item) ----

        private static SalesInvoiceResponseDto Stored(long id, decimal salePrice, decimal tax, decimal fees, decimal discount, decimal paid) => new()
        {
            Id = id,
            Sale_Price = salePrice,
            Tax = tax,
            Fees = fees,
            Discount = discount,
            Paid = paid,
            Lifecycle = InvoiceLifecycle.Posted
        };

        [Fact]
        public async Task GetAllSalesInvoices_CalculatesTotalAndBalanceForEveryItem()
        {
            _repository.Page = new PagedList<SalesInvoiceResponseDto>(new List<SalesInvoiceResponseDto>
            {
                Stored(1, 20000m, 1500m, 300m, 500m, 5000m),   // total 21 300, balance 16 300
                Stored(2, 10000m, 0m, 200m, 0m, 10200m),       // total 10 200, balance 0
                Stored(3, 30000m, 2000m, 0m, 1000m, 1000m)     // total 31 000, balance 30 000
            }, 3, 1, 5);

            var result = await _service.GetAllSalesInvoices(new SalesInvoiceParameters());

            Assert.Equal(new[] { 21300m, 10200m, 31000m }, result.Select(i => i.Total));
            Assert.Equal(new[] { 16300m, 0m, 30000m }, result.Select(i => i.Balance));
        }

        [Fact]
        public async Task GetAllSalesInvoices_OverpaidInvoice_BalanceIsNegativeNotClamped()
        {
            _repository.Page = new PagedList<SalesInvoiceResponseDto>(
                new List<SalesInvoiceResponseDto> { Stored(1, 1000m, 0m, 0m, 0m, 1500m) }, 1, 1, 5);

            var result = await _service.GetAllSalesInvoices(new SalesInvoiceParameters());

            Assert.Equal(-500m, Assert.Single(result).Balance);
        }

        // ---- Story 9: GetSalesInvoiceById ----

        [Fact]
        public async Task GetSalesInvoiceById_CalculatesTotal_SalePricePlusTaxPlusFeesMinusDiscount()
        {
            _repository.ByIdResult = Stored(1, 20000m, 1500m, 300m, 500m, 0m);

            var result = await _service.GetSalesInvoiceById(1);

            Assert.Equal(21300m, result!.Total);
        }

        [Fact]
        public async Task GetSalesInvoiceById_TotalKeepsCents()
        {
            _repository.ByIdResult = Stored(1, 19999.99m, 1200.50m, 99.25m, 0.74m, 0m);

            var result = await _service.GetSalesInvoiceById(1);

            Assert.Equal(21299.00m, result!.Total);
        }

        [Fact]
        public async Task GetSalesInvoiceById_BalanceSubtractsPaid()
        {
            _repository.ByIdResult = Stored(1, 20000m, 1500m, 300m, 500m, 5000m);

            var result = await _service.GetSalesInvoiceById(1);

            Assert.Equal(16300m, result!.Balance);
        }

        [Fact]
        public async Task GetSalesInvoiceById_FullyPaid_BalanceIsZero()
        {
            _repository.ByIdResult = Stored(1, 20000m, 1500m, 300m, 500m, 21300m);

            var result = await _service.GetSalesInvoiceById(1);

            Assert.Equal(0m, result!.Balance);
        }

        // ---- Story 9: CancelSalesInvoice ----

        [Fact]
        public async Task CancelSalesInvoice_CalculatesTotalAndBalanceOnReturnedInvoice()
        {
            var result = await _service.CancelSalesInvoice(7, new SalesInvoiceCancelDto { Reason = "Wrong customer" });

            Assert.Equal(21300m, result.Total);
            Assert.Equal(21300m, result.Balance);
        }

        [Fact]
        public async Task CancelSalesInvoice_PassesIdAndTrimmedReasonToRepository()
        {
            await _service.CancelSalesInvoice(7, new SalesInvoiceCancelDto { Reason = "  Wrong customer  " });

            var call = Assert.Single(_repository.CancelCalls);
            Assert.Equal(7, call.Id);
            Assert.Equal("Wrong customer", call.Reason.Reason);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public async Task CancelSalesInvoice_BlankReason_ThrowsAndDoesNotCallRepository(string reason)
        {
            var ex = await Assert.ThrowsAsync<ArgumentException>(
                () => _service.CancelSalesInvoice(7, new SalesInvoiceCancelDto { Reason = reason }));

            Assert.Equal("A cancellation reason is required.", ex.Message);
            Assert.Empty(_repository.CancelCalls);
        }

        [Fact]
        public async Task CancelSalesInvoice_NullDto_ThrowsAndDoesNotCallRepository()
        {
            await Assert.ThrowsAsync<ArgumentException>(() => _service.CancelSalesInvoice(7, null!));

            Assert.Empty(_repository.CancelCalls);
        }

        [Fact]
        public async Task CancelSalesInvoice_Reason501Characters_ThrowsAndDoesNotCallRepository()
        {
            var ex = await Assert.ThrowsAsync<ArgumentException>(
                () => _service.CancelSalesInvoice(7, new SalesInvoiceCancelDto { Reason = new string('a', 501) }));

            Assert.Equal("The cancellation reason cannot exceed 500 characters.", ex.Message);
            Assert.Empty(_repository.CancelCalls);
        }

    }
}
