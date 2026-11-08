namespace NPMotors.Repository.Models.Dto
{
    public class TransferResponseDto
    {
        public string Transfer_Reference { get; set; } = string.Empty;
        public long Out_Transaction_Id { get; set; }
        public long In_Transaction_Id { get; set; }
        public decimal Amount { get; set; }
        public DateTime Date { get; set; }
        public string Source_Account_Name { get; set; } = string.Empty;
        public decimal Source_Book_Balance { get; set; }
        public string Destination_Account_Name { get; set; } = string.Empty;
        public decimal Destination_Book_Balance { get; set; }
    }
}
