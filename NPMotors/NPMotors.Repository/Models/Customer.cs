using System;
using System.Collections.Generic;
using System.Text;

namespace NPMotors.Repository.Models
{
    public class Customer
    {
        public long Id { get; set; }
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public string Phone { get; set; }
        public string Email { get; set; }
        public string Address { get; set; }
        public string City { get; set; }
        public string State { get; set; }
        public string Zip { get; set; }
        public decimal Balance { get; set; }
        public decimal TotalPurchases { get; set; }
        public string Status { get; set; }
    }
}
