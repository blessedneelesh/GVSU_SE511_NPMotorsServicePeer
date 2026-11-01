using NPMotors.Repository.Interface;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.Models.Enums;
using NPMotors.Services.Service;

namespace Module.Tests.Services
{
    public class SalesInvoiceServiceTest
    {
        // Hand-written fake: the test projects do not use a mocking library.
        private class FakeSalesInvoiceRepository : ISalesInvoiceRepository
        {
            public SalesInvoiceResponseDto? Result { get; set; }
            public List<SalesInvoiceCreateDto> AddCalls { get; } = new();

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
                    Sale_Price = invoice.Sale_Price,
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
            _service = new SalesInvoiceService(_repository);
        }

        private static SalesInvoiceCreateDto NewInvoice(
            decimal salePrice = 20000m, decimal tax = 0m, decimal fees = 0m, decimal discount = 0m) => new()
        {
            Customer_Id = 1,
            Salesperson_Id = 2,
            Vehicle_Id = 3,
            Invoice_Date = new DateTime(2026, 5, 1),
            Due_Date = new DateTime(2026, 5, 31),
            Sale_Price = salePrice,
            Tax = tax,
            Fees = fees,
            Discount = discount
        };

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
        public async Task AddSalesInvoice_TotalWithOnlySalePrice_EqualsSalePrice()
        {
            var result = await _service.AddSalesInvoice(NewInvoice(20000m));

            Assert.Equal(20000m, result.Total);
        }

        [Fact]
        public async Task AddSalesInvoice_TaxIncreasesTotal()
        {
            var result = await _service.AddSalesInvoice(NewInvoice(20000m, tax: 1500m));

            Assert.Equal(21500m, result.Total);
        }

        [Fact]
        public async Task AddSalesInvoice_FeesIncreaseTotal()
        {
            var result = await _service.AddSalesInvoice(NewInvoice(20000m, fees: 300m));

            Assert.Equal(20300m, result.Total);
        }

        [Fact]
        public async Task AddSalesInvoice_DiscountDecreasesTotal()
        {
            var result = await _service.AddSalesInvoice(NewInvoice(20000m, discount: 500m));

            Assert.Equal(19500m, result.Total);
        }

        [Fact]
        public async Task AddSalesInvoice_TotalKeepsCents()
        {
            var result = await _service.AddSalesInvoice(NewInvoice(19999.99m, 1200.50m, 99.25m, 0.74m));

            Assert.Equal(21299.00m, result.Total);
        }

        // ---- CalculateBalance via AddSalesInvoice ----

        [Fact]
        public async Task AddSalesInvoice_DraftBalanceEqualsTotal()
        {
            var result = await _service.AddSalesInvoice(NewInvoice(20000m, 1500m, 300m, 500m));

            Assert.Equal(0m, result.Paid);
            Assert.Equal(21300m, result.Balance);
        }

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
        public async Task AddSalesInvoice_DiscountExceedsSalePrice_Throws()
        {
            await AssertRejectedAsync(NewInvoice(1000m, tax: 500m, discount: 1000.01m),
                "Discount cannot exceed the sale price.");
        }

        [Fact]
        public async Task AddSalesInvoice_DiscountEqualsSalePriceWithFees_IsAllowed()
        {
            var result = await _service.AddSalesInvoice(NewInvoice(1000m, fees: 50m, discount: 1000m));

            Assert.Equal(50m, result.Total);
            Assert.Single(_repository.AddCalls);
        }

        [Fact]
        public async Task AddSalesInvoice_DiscountBelowSalePrice_IsAllowed()
        {
            var result = await _service.AddSalesInvoice(NewInvoice(1000m, discount: 999.99m));

            Assert.Equal(0.01m, result.Total);
        }

        [Fact]
        public async Task AddSalesInvoice_TotalIsZero_Throws()
        {
            await AssertRejectedAsync(NewInvoice(1000m, discount: 1000m),
                "Invoice total must be greater than zero.");
        }

        [Fact]
        public async Task AddSalesInvoice_SalePriceZero_Throws()
        {
            await AssertRejectedAsync(NewInvoice(0m),
                "Invoice total must be greater than zero.");
        }

        [Fact]
        public async Task AddSalesInvoice_SalePriceZeroWithTaxAndFees_PositiveTotal_IsAllowed()
        {
            var result = await _service.AddSalesInvoice(NewInvoice(0m, tax: 100m, fees: 50m));

            Assert.Equal(150m, result.Total);
        }

        [Fact]
        public async Task AddSalesInvoice_NegativeSalePrice_Throws()
        {
            await AssertRejectedAsync(NewInvoice(-1m), "Sale price cannot be negative.");
        }

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
        [InlineData(-5)]
        public async Task AddSalesInvoice_MissingCustomer_Throws(long customerId)
        {
            var invoice = NewInvoice();
            invoice.Customer_Id = customerId;

            await AssertRejectedAsync(invoice, "Customer is required.");
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-5)]
        public async Task AddSalesInvoice_MissingSalesperson_Throws(long salespersonId)
        {
            var invoice = NewInvoice();
            invoice.Salesperson_Id = salespersonId;

            await AssertRejectedAsync(invoice, "Salesperson is required.");
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-5)]
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
    }
}
