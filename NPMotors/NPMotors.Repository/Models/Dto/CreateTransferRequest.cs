using System.ComponentModel.DataAnnotations;

namespace NPMotors.Repository.Models.Dto
{
    public class CreateTransferRequest
    {
        public long SourceAccountId { get; set; }
        public long DestinationAccountId { get; set; }
        public decimal Amount { get; set; }
        public DateTime Date { get; set; }
        [StringLength(500)]
        public string? Description { get; set; }
    }
}
