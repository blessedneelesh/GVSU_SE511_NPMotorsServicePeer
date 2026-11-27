namespace NPMotors.Repository.Models.Dto
{
    public class EmployeePayrollDto
    {
        public long Employee_Id { get; set; }
        public string Employee_Name { get; set; } = string.Empty;
        // Annual amounts, prorated by months in the period.
        public decimal Salary { get; set; }
        public decimal Bonus { get; set; }
        // 0-100.
        public decimal Commission_Rate { get; set; }
        public decimal Commission_Sales { get; set; }
    }
}
