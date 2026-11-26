using System.ComponentModel.DataAnnotations;
using System.Data;
using NPMotors.Repository.Interface;
using NPMotors.Repository.Models;
using NPMotors.Repository.Models.Dto;
using NPMotors.Services.Service;

namespace Module.Tests.Services
{
    // Story 21, RecordMiscellaneousPayment. ValidateMiscPayment and IsOperatingExpense are private static;
    // they are tested only through the public method. Validation runs first and a violation stops the call
    // before the repository (bank account check and atomic save) is reached.
    public class BankPaymentServiceTest
    {
        private class FakeBankPaymentRepository : IBankPaymentRepository
        {
            public BankPaymentResponseDto Response { get; set; } = new()
            {
                Id = 5, Payment_Number = "PAY-2026-000005", Payee = "City Landlord", Payment_Category = "Rent",
                Amount = 2500m, Book_Balance = 47500m, Status = "Posted"
            };
            public Exception? Throw { get; set; }
            public List<MiscellaneousPaymentCreateDto> Calls { get; } = new();

            public Task<BankPaymentResponseDto> RecordMiscellaneousPayment(MiscellaneousPaymentCreateDto payment)
            {
                Calls.Add(payment);
                if (Throw != null) throw Throw;
                return Task.FromResult(Response);
            }

            public Task<long> InsertBankPayment(BankPayment payment, IDbTransaction tx) => throw new NotSupportedException();
            public Task<IEnumerable<BankPayment>> GetPaymentsByBillId(long apBillId) => throw new NotSupportedException();
        }

        private readonly FakeBankPaymentRepository _repository = new();

        private BankPaymentService CreateService() => new(_repository);

        private static MiscellaneousPaymentCreateDto Payment() => new()
        {
            Payee = "City Landlord", Payment_Category = "Rent", Payment_Date = new DateTime(2026, 11, 8),
            Amount = 2500m, Payment_Method = "Transfer", Bank_Account_Id = 3
        };

        private async Task<string> RejectedAsync(MiscellaneousPaymentCreateDto payment)
        {
            var ex = await Assert.ThrowsAsync<ValidationException>(() => CreateService().RecordMiscellaneousPayment(payment));
            Assert.Empty(_repository.Calls);
            return ex.Message;
        }

        // ---- public method: mapping to the repository ----

        [Fact]
        public async Task RecordMiscellaneousPayment_Valid_PassesTheSameDtoToTheRepositoryOnce()
        {
            var payment = Payment();

            await CreateService().RecordMiscellaneousPayment(payment);

            Assert.Same(payment, Assert.Single(_repository.Calls));
        }

        [Fact]
        public async Task RecordMiscellaneousPayment_Valid_ReturnsTheRepositoryResponse()
        {
            var result = await CreateService().RecordMiscellaneousPayment(Payment());

            Assert.Same(_repository.Response, result);
            Assert.Equal(47500m, result.Book_Balance);
        }

        [Fact]
        public async Task RecordMiscellaneousPayment_Valid_DoesNotChangeTheDto()
        {
            var payment = Payment();
            payment.Payee = "  City Landlord  ";
            payment.Payment_Method = " transfer ";

            await CreateService().RecordMiscellaneousPayment(payment);

            Assert.Equal("  City Landlord  ", payment.Payee);
            Assert.Equal(" transfer ", payment.Payment_Method);
            Assert.Equal(2500m, payment.Amount);
        }

        [Fact]
        public async Task RecordMiscellaneousPayment_RepositoryBusinessRuleViolation_ReachesTheCaller()
        {
            _repository.Throw = new ValidationException("Amount cannot exceed the bank account's book balance.");

            var ex = await Assert.ThrowsAsync<ValidationException>(() => CreateService().RecordMiscellaneousPayment(Payment()));

            Assert.Equal("Amount cannot exceed the bank account's book balance.", ex.Message);
            Assert.Single(_repository.Calls);
        }

        [Fact]
        public async Task RecordMiscellaneousPayment_UnexpectedRepositoryError_IsNotSwallowed()
        {
            _repository.Throw = new InvalidOperationException("db down");

            await Assert.ThrowsAsync<InvalidOperationException>(() => CreateService().RecordMiscellaneousPayment(Payment()));
        }

        [Fact]
        public async Task RecordMiscellaneousPayment_TwoCalls_AreTwoSeparateRepositoryCalls()
        {
            var service = CreateService();

            await service.RecordMiscellaneousPayment(Payment());
            await service.RecordMiscellaneousPayment(Payment());

            Assert.Equal(2, _repository.Calls.Count);
        }

        // ---- ValidateMiscPayment: Payee and Payment_Category not blank ----

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public async Task RecordMiscellaneousPayment_PayeeBlank_ThrowsAndNeverReachesTheRepository(string payee)
        {
            var payment = Payment();
            payment.Payee = payee;

            Assert.Equal("Payee is required.", await RejectedAsync(payment));
        }

        [Fact]
        public async Task RecordMiscellaneousPayment_PayeeNull_ThrowsAndNeverReachesTheRepository()
        {
            var payment = Payment();
            payment.Payee = null!;

            Assert.Equal("Payee is required.", await RejectedAsync(payment));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public async Task RecordMiscellaneousPayment_CategoryBlank_ThrowsAndNeverReachesTheRepository(string category)
        {
            var payment = Payment();
            payment.Payment_Category = category;

            Assert.Equal("Payment category is required.", await RejectedAsync(payment));
        }

        [Fact]
        public async Task RecordMiscellaneousPayment_CategoryNull_ThrowsAndNeverReachesTheRepository()
        {
            var payment = Payment();
            payment.Payment_Category = null!;

            Assert.Equal("Payment category is required.", await RejectedAsync(payment));
        }

        // ---- ValidateMiscPayment: Amount > 0 ----

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(-2500)]
        public async Task RecordMiscellaneousPayment_AmountNotAboveZero_ThrowsAndNeverReachesTheRepository(double amount)
        {
            var payment = Payment();
            payment.Amount = (decimal)amount;

            Assert.Equal("Amount must be greater than zero.", await RejectedAsync(payment));
        }

        [Fact]
        public async Task RecordMiscellaneousPayment_AmountOneCent_IsAccepted()
        {
            var payment = Payment();
            payment.Amount = 0.01m;

            await CreateService().RecordMiscellaneousPayment(payment);

            Assert.Single(_repository.Calls);
        }

        // ---- ValidateMiscPayment: Payment_Method is Cash | Check | Card | Transfer ----

        [Theory]
        [InlineData("Cash")]
        [InlineData("Check")]
        [InlineData("Card")]
        [InlineData("Transfer")]
        [InlineData("cash")]
        [InlineData("CHECK")]
        [InlineData(" card ")]
        public async Task RecordMiscellaneousPayment_ValidMethod_IsAccepted(string method)
        {
            var payment = Payment();
            payment.Payment_Method = method;

            await CreateService().RecordMiscellaneousPayment(payment);

            Assert.Single(_repository.Calls);
        }

        [Theory]
        [InlineData("Cheque")]
        [InlineData("Bitcoin")]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("Wire")]
        public async Task RecordMiscellaneousPayment_InvalidMethod_ThrowsAndNeverReachesTheRepository(string method)
        {
            var payment = Payment();
            payment.Payment_Method = method;

            Assert.Equal("Payment method must be Cash, Check, Card or Transfer.", await RejectedAsync(payment));
        }

        [Fact]
        public async Task RecordMiscellaneousPayment_MethodNull_ThrowsAndNeverReachesTheRepository()
        {
            var payment = Payment();
            payment.Payment_Method = null!;

            Assert.Equal("Payment method must be Cash, Check, Card or Transfer.", await RejectedAsync(payment));
        }

        [Fact]
        public async Task RecordMiscellaneousPayment_NumericMethod_IsRejected()
        {
            var payment = Payment();
            payment.Payment_Method = "1";

            Assert.Equal("Payment method must be Cash, Check, Card or Transfer.", await RejectedAsync(payment));
        }

        // ---- ValidateMiscPayment: Bank_Account_Id given ----

        [Theory]
        [InlineData(0)]
        [InlineData(-3)]
        public async Task RecordMiscellaneousPayment_BankAccountNotGiven_ThrowsAndNeverReachesTheRepository(long accountId)
        {
            var payment = Payment();
            payment.Bank_Account_Id = accountId;

            Assert.Equal("A bank account is required.", await RejectedAsync(payment));
        }

        [Fact]
        public async Task RecordMiscellaneousPayment_BankAccountOne_IsAccepted()
        {
            var payment = Payment();
            payment.Bank_Account_Id = 1;

            await CreateService().RecordMiscellaneousPayment(payment);

            Assert.Single(_repository.Calls);
        }

        // ---- IsOperatingExpense (called by ValidateMiscPayment, AC 4) ----

        [Theory]
        [InlineData("Rent")]
        [InlineData("Utilities")]
        [InlineData("Supplies")]
        [InlineData("Payroll")]
        [InlineData("Office Insurance")]
        public async Task RecordMiscellaneousPayment_OperatingExpenseCategory_IsAccepted(string category)
        {
            var payment = Payment();
            payment.Payment_Category = category;

            await CreateService().RecordMiscellaneousPayment(payment);

            Assert.Single(_repository.Calls);
        }

        [Theory]
        [InlineData("Vehicle Purchase")]
        [InlineData("AP Payment")]
        public async Task RecordMiscellaneousPayment_InventoryPurchaseOrApSettlementCategory_ThrowsAndNeverReachesTheRepository(string category)
        {
            var payment = Payment();
            payment.Payment_Category = category;

            Assert.Equal("Inventory purchases and AP settlements are not operating expenses.", await RejectedAsync(payment));
        }

        [Theory]
        [InlineData("vehicle purchase")]
        [InlineData("AP PAYMENT")]
        [InlineData("  Vehicle Purchase  ")]
        [InlineData(" ap payment")]
        public async Task RecordMiscellaneousPayment_NonOperatingCategory_IsRejectedWhateverTheCasingOrSpacing(string category)
        {
            var payment = Payment();
            payment.Payment_Category = category;

            Assert.Equal("Inventory purchases and AP settlements are not operating expenses.", await RejectedAsync(payment));
        }

        [Fact]
        public async Task RecordMiscellaneousPayment_NonOperatingCategoryWithOtherwiseValidPayment_OnlyTheCategoryIsWrong()
        {
            var payment = Payment();
            payment.Payment_Category = "Vehicle Purchase";
            payment.Vendor_Id = 4;
            payment.Reference = "INV-1";

            await RejectedAsync(payment);

            payment.Payment_Category = "Supplies";
            await CreateService().RecordMiscellaneousPayment(payment);
            Assert.Single(_repository.Calls);
        }

        // ---- order of the checks ----

        [Fact]
        public async Task RecordMiscellaneousPayment_SeveralViolations_ReportsThePayeeFirst()
        {
            var payment = Payment();
            payment.Payee = "";
            payment.Amount = 0m;
            payment.Payment_Category = "AP Payment";

            Assert.Equal("Payee is required.", await RejectedAsync(payment));
        }

        [Fact]
        public async Task RecordMiscellaneousPayment_AmountAndCategoryBothWrong_ReportsTheAmountFirst()
        {
            var payment = Payment();
            payment.Amount = -1m;
            payment.Payment_Category = "AP Payment";

            Assert.Equal("Amount must be greater than zero.", await RejectedAsync(payment));
        }

        [Fact]
        public async Task RecordMiscellaneousPayment_FieldsNotChecked_AreNotValidatedHere()
        {
            // Date, vendor, reference and description rules belong to the DTO annotations and the repository.
            var payment = Payment();
            payment.Vendor_Id = long.MaxValue;
            payment.Reference = new string('x', 500);
            payment.Description = new string('y', 500);

            await CreateService().RecordMiscellaneousPayment(payment);

            Assert.Single(_repository.Calls);
        }
    }
}
