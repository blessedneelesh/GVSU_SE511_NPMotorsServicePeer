using System;
using System.Collections.Generic;
using System.Text;
using NPMotors.Repository.Models.Enums;

namespace NPMotors.Repository.Models.Dto
{
    public class BankAccountResponseDto
    {
        public long Id { get; set; }
        public long BankId { get; set; }
        public string? BankName { get; set; }
        public string? Name { get; set; }
        // Masked (e.g. ******1234) in list responses, full value in single-record responses.
        public string? AccountNumber { get; set; }
        public string? Type { get; set; }
        public string? Routing { get; set; }
        public decimal BookBalance { get; set; }
        public Status? Status { get; set; }
    }
}
