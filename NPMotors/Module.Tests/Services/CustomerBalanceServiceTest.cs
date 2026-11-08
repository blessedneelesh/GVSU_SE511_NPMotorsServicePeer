using System.Data;
using NPMotors.Repository.Interface;
using NPMotors.Repository.Models.Dto;
using NPMotors.Repository.RequestFeatures;
using NPMotors.Services.Service;

namespace Module.Tests.Services
{
    // Story 18, UpdateCustomerTotals. RecalculateCustomerBalance and RecalculateTotalPurchases are private static;
    // they are tested only through UpdateCustomerTotals, by reading what is written to the repository.
    public class CustomerBalanceServiceTest
    {
        private class FakeDbTransaction : IDbTransaction
        {
            public IDbConnection? Connection => null;
            public IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
            public void Commit() { }
            public void Rollback() { }
            public void Dispose() { }
        }

        private class FakeCustomerRepository : ICustomerRepository
        {
            private readonly List<string> _log;
            public FakeCustomerRepository(List<string> log) { _log = log; }

            public decimal[] OpenArBalances { get; set; } = Array.Empty<decimal>();
            public decimal[] PostedInvoiceTotals { get; set; } = Array.Empty<decimal>();
            public Exception? ThrowOnOpenAr { get; set; }
            public Exception? ThrowOnPosted { get; set; }
            public List<(long CustomerId, IDbTransaction Tx)> OpenArCalls { get; } = new();
            public List<(long CustomerId, IDbTransaction Tx)> PostedCalls { get; } = new();
            public List<(long CustomerId, decimal Balance, decimal TotalPurchases, IDbTransaction Tx)> UpdateCalls { get; } = new();

            public Task<IEnumerable<decimal>> GetOpenArBalances(long customerId, IDbTransaction tx)
            {
                _log.Add("get-open-ar");
                OpenArCalls.Add((customerId, tx));
                if (ThrowOnOpenAr is not null)
                    throw ThrowOnOpenAr;
                return Task.FromResult<IEnumerable<decimal>>(OpenArBalances);
            }

            public Task<IEnumerable<decimal>> GetPostedInvoiceTotals(long customerId, IDbTransaction tx)
            {
                _log.Add("get-posted-totals");
                PostedCalls.Add((customerId, tx));
                if (ThrowOnPosted is not null)
                    throw ThrowOnPosted;
                return Task.FromResult<IEnumerable<decimal>>(PostedInvoiceTotals);
            }

            public Task<int> UpdateCustomerTotals(long customerId, decimal balance, decimal totalPurchases, IDbTransaction tx)
            {
                _log.Add("update-totals");
                UpdateCalls.Add((customerId, balance, totalPurchases, tx));
                return Task.FromResult(1);
            }

            public Task<PagedList<CustomerResponseDto>> GetAllCustomers(CustomerParameters customerParameters) => throw new NotSupportedException();
            public Task<CustomerResponseDto?> GetCustomerById(long id) => throw new NotSupportedException();
            public Task<CustomerResponseDto> AddCustomer(CustomerCreateDto customer) => throw new NotSupportedException();
            public Task<CustomerResponseDto?> UpdateCustomer(long id, CustomerUpdateDto customer) => throw new NotSupportedException();
            public Task<bool> DeleteCustomer(long id) => throw new NotSupportedException();
        }

        private readonly List<string> _log = new();
        private readonly FakeCustomerRepository _repository;
        private readonly CustomerBalanceService _service;
        private readonly IDbTransaction _tx = new FakeDbTransaction();

        public CustomerBalanceServiceTest()
        {
            _repository = new FakeCustomerRepository(_log);
            _service = new CustomerBalanceService(_repository);
        }

        private (decimal Balance, decimal TotalPurchases) Written()
        {
            var call = Assert.Single(_repository.UpdateCalls);
            return (call.Balance, call.TotalPurchases);
        }

        // ---- RecalculateCustomerBalance (AC 1, AC 4) ----

        [Fact]
        public async Task UpdateCustomerTotals_BalanceIsSumOfOpenArBalances()
        {
            _repository.OpenArBalances = new[] { 100.50m, 200.25m, 49.25m };

            await _service.UpdateCustomerTotals(5, _tx);

            Assert.Equal(350.00m, Written().Balance);
        }

        [Fact]
        public async Task UpdateCustomerTotals_SingleOpenAr_BalanceIsThatAmount()
        {
            _repository.OpenArBalances = new[] { 13500m };

            await _service.UpdateCustomerTotals(5, _tx);

            Assert.Equal(13500m, Written().Balance);
        }

        [Fact]
        public async Task UpdateCustomerTotals_NoOpenAr_BalanceIsZero()
        {
            await _service.UpdateCustomerTotals(5, _tx);

            Assert.Equal(0m, Written().Balance);
        }

        [Fact]
        public async Task UpdateCustomerTotals_NegativeSum_BalanceIsClampedToZero()
        {
            _repository.OpenArBalances = new[] { -50m, 20m };

            await _service.UpdateCustomerTotals(5, _tx);

            Assert.Equal(0m, Written().Balance);
        }

        [Fact]
        public async Task UpdateCustomerTotals_SingleNegativeBalance_BalanceIsZeroNotNegative()
        {
            _repository.OpenArBalances = new[] { -0.01m };

            await _service.UpdateCustomerTotals(5, _tx);

            Assert.Equal(0m, Written().Balance);
        }

        [Fact]
        public async Task UpdateCustomerTotals_SumExactlyZero_BalanceIsZero()
        {
            _repository.OpenArBalances = new[] { -30m, 30m };

            await _service.UpdateCustomerTotals(5, _tx);

            Assert.Equal(0m, Written().Balance);
        }

        [Fact]
        public async Task UpdateCustomerTotals_PositiveSumWithNegativeEntry_KeepsPositiveRemainder()
        {
            _repository.OpenArBalances = new[] { 100m, -40m };

            await _service.UpdateCustomerTotals(5, _tx);

            Assert.Equal(60m, Written().Balance);
        }

        [Fact]
        public async Task UpdateCustomerTotals_BalanceIgnoresPostedInvoiceTotals()
        {
            _repository.OpenArBalances = new[] { 500m };
            _repository.PostedInvoiceTotals = new[] { 9000m, 8000m };

            await _service.UpdateCustomerTotals(5, _tx);

            Assert.Equal(500m, Written().Balance);
        }

        // ---- RecalculateTotalPurchases (AC 2) ----

        [Fact]
        public async Task UpdateCustomerTotals_TotalPurchasesIsSumOfPostedInvoiceTotals()
        {
            _repository.PostedInvoiceTotals = new[] { 10000.50m, 20000m, 499.50m };

            await _service.UpdateCustomerTotals(5, _tx);

            Assert.Equal(30500.00m, Written().TotalPurchases);
        }

        [Fact]
        public async Task UpdateCustomerTotals_SinglePostedInvoice_TotalPurchasesIsItsTotal()
        {
            _repository.PostedInvoiceTotals = new[] { 18500m };

            await _service.UpdateCustomerTotals(5, _tx);

            Assert.Equal(18500m, Written().TotalPurchases);
        }

        [Fact]
        public async Task UpdateCustomerTotals_NoPostedInvoices_TotalPurchasesIsZero()
        {
            await _service.UpdateCustomerTotals(5, _tx);

            Assert.Equal(0m, Written().TotalPurchases);
        }

        [Fact]
        public async Task UpdateCustomerTotals_TotalPurchasesIgnoresOpenArBalances()
        {
            _repository.OpenArBalances = new[] { 700m };
            _repository.PostedInvoiceTotals = new[] { 1000m };

            await _service.UpdateCustomerTotals(5, _tx);

            Assert.Equal(1000m, Written().TotalPurchases);
        }

        [Fact]
        public async Task UpdateCustomerTotals_TotalPurchasesIsNotClamped_WhenTotalsAreFullyPaid()
        {
            // A fully paid sale has no open AR (balance 0) but still counts as a purchase.
            _repository.OpenArBalances = Array.Empty<decimal>();
            _repository.PostedInvoiceTotals = new[] { 25000m };

            await _service.UpdateCustomerTotals(5, _tx);

            Assert.Equal((0m, 25000m), Written());
        }

        // ---- UpdateCustomerTotals as a whole ----

        [Fact]
        public async Task UpdateCustomerTotals_WritesBothTotalsInOneCall()
        {
            _repository.OpenArBalances = new[] { 1500m, 2500m };
            _repository.PostedInvoiceTotals = new[] { 12000m, 8000m };

            await _service.UpdateCustomerTotals(5, _tx);

            Assert.Equal((4000m, 20000m), Written());
        }

        [Fact]
        public async Task UpdateCustomerTotals_PassesCustomerIdAndTransactionToEveryRepositoryCall()
        {
            await _service.UpdateCustomerTotals(42, _tx);

            Assert.Equal(42, Assert.Single(_repository.OpenArCalls).CustomerId);
            Assert.Equal(42, Assert.Single(_repository.PostedCalls).CustomerId);
            Assert.Equal(42, Assert.Single(_repository.UpdateCalls).CustomerId);
            Assert.Same(_tx, _repository.OpenArCalls[0].Tx);
            Assert.Same(_tx, _repository.PostedCalls[0].Tx);
            Assert.Same(_tx, _repository.UpdateCalls[0].Tx);
        }

        [Fact]
        public async Task UpdateCustomerTotals_CallsRepositoryInTheDiagramOrder()
        {
            await _service.UpdateCustomerTotals(5, _tx);

            Assert.Equal(new[] { "get-open-ar", "get-posted-totals", "update-totals" }, _log);
        }

        [Fact]
        public async Task UpdateCustomerTotals_CalledTwiceWithSameData_WritesSameValuesBothTimes()
        {
            // Recalculated from source rows, never added to what is stored, so a repeated call is harmless.
            _repository.OpenArBalances = new[] { 300m };
            _repository.PostedInvoiceTotals = new[] { 900m };

            await _service.UpdateCustomerTotals(5, _tx);
            await _service.UpdateCustomerTotals(5, _tx);

            Assert.Equal(2, _repository.UpdateCalls.Count);
            Assert.Equal((300m, 900m), (_repository.UpdateCalls[0].Balance, _repository.UpdateCalls[0].TotalPurchases));
            Assert.Equal((300m, 900m), (_repository.UpdateCalls[1].Balance, _repository.UpdateCalls[1].TotalPurchases));
        }

        [Fact]
        public async Task UpdateCustomerTotals_OpenArReadFails_ThrowsAndWritesNothing()
        {
            _repository.ThrowOnOpenAr = new InvalidOperationException("db down");

            await Assert.ThrowsAsync<InvalidOperationException>(() => _service.UpdateCustomerTotals(5, _tx));

            Assert.Empty(_repository.UpdateCalls);
        }

        [Fact]
        public async Task UpdateCustomerTotals_PostedTotalsReadFails_ThrowsAndWritesNothing()
        {
            _repository.ThrowOnPosted = new InvalidOperationException("db down");

            await Assert.ThrowsAsync<InvalidOperationException>(() => _service.UpdateCustomerTotals(5, _tx));

            Assert.Empty(_repository.UpdateCalls);
        }
    }
}
