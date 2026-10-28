using NPMotors.Repository.Interface;
using System;
using System.Collections.Generic;
using System.Data;
using System.Text;
using NPMotors.Repository.Models.Dto;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using NPMotors.Repository.RequestFeatures;

namespace NPMotors.Repository.Repositories
{
    public class CustomerRepository: ICustomerRepository
    {
        private readonly IRepositoryContext _repositoryContext;

        public CustomerRepository(IRepositoryContext repositoryContext)
        {
            _repositoryContext = repositoryContext;
        }

        public async Task<PagedList<CustomerResponseDto>> GetAllCustomers(CustomerParameters customerParameters)
        {
            using IDbConnection connection = _repositoryContext.GetDatabaseConnection();

            var whereClauses = new List<string>();
            var queryParams = new DynamicParameters();

            var status = customerParameters.Status?.Trim();
            if (!string.IsNullOrWhiteSpace(status) &&
                (status.Equals("active", StringComparison.OrdinalIgnoreCase) ||
                 status.Equals("inactive", StringComparison.OrdinalIgnoreCase)))
            {
                whereClauses.Add("LOWER(status) = @Status");
                queryParams.Add("Status", status.ToLowerInvariant());
            }

            var searchTerm = customerParameters.SearchTerm?.Trim();
            if (!string.IsNullOrWhiteSpace(searchTerm))
            {
                whereClauses.Add(@"(
                    LOWER(first_name) LIKE @SearchTerm OR
                    LOWER(last_name) LIKE @SearchTerm OR
                    LOWER(email) LIKE @SearchTerm OR
                    LOWER(phone) LIKE @SearchTerm
                )");
                queryParams.Add("SearchTerm", $"%{searchTerm.ToLowerInvariant()}%");
            }

            var whereSql = whereClauses.Count > 0
                ? " WHERE " + string.Join(" AND ", whereClauses)
                : string.Empty;

            var count = await connection.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM customer{whereSql}", queryParams);

            queryParams.Add("Offset", (customerParameters.PageNumber - 1) * customerParameters.PageSize);
            queryParams.Add("Limit", customerParameters.PageSize);

            var customers = (await connection.QueryAsync<CustomerResponseDto>(
                $"SELECT * FROM customer{whereSql} OFFSET @Offset LIMIT @Limit",
                queryParams)).ToList();

            return new PagedList<CustomerResponseDto>(
                customers,
                count,
                customerParameters.PageNumber,
                customerParameters.PageSize);
        }

        private const string CustomerColumns =
            "id, first_name AS First_Name, last_name AS Last_Name, phone, email, address, city, state, zip, " +
            "balance, total_purchases AS TotalPurchases, status";

        public async Task<CustomerResponseDto?> GetCustomerById(long id)
        {
            using IDbConnection connection = _repositoryContext.GetDatabaseConnection();

            return await connection.QuerySingleOrDefaultAsync<CustomerResponseDto>(
                $"SELECT {CustomerColumns} FROM customer WHERE id = @Id",
                new { Id = id });
        }

        public async Task<CustomerResponseDto> AddCustomer(CustomerCreateDto customer)
        {
            using IDbConnection connection = _repositoryContext.GetDatabaseConnection();

            var queryParams = new DynamicParameters();
            queryParams.Add("FirstName", customer.First_Name.Trim());
            queryParams.Add("LastName", customer.Last_Name.Trim());
            queryParams.Add("Phone", customer.Phone?.Trim());
            queryParams.Add("Email", customer.Email.Trim());
            queryParams.Add("Address", customer.Address?.Trim());
            queryParams.Add("City", customer.City?.Trim());
            queryParams.Add("State", customer.State?.Trim());
            queryParams.Add("Zip", customer.Zip?.Trim());
            queryParams.Add("Status", (customer.Status ?? "active").Trim().ToLowerInvariant());

            return await connection.QuerySingleAsync<CustomerResponseDto>(
                $@"INSERT INTO customer (first_name, last_name, phone, email, address, city, state, zip, balance, total_purchases, status)
                   VALUES (@FirstName, @LastName, @Phone, @Email, @Address, @City, @State, @Zip, 0, 0, @Status)
                   RETURNING {CustomerColumns}",
                queryParams);
        }

        public async Task<CustomerResponseDto?> UpdateCustomer(long id, CustomerUpdateDto customer)
        {
            using IDbConnection connection = _repositoryContext.GetDatabaseConnection();

            var queryParams = new DynamicParameters();
            queryParams.Add("Id", id);
            queryParams.Add("FirstName", customer.First_Name.Trim());
            queryParams.Add("LastName", customer.Last_Name.Trim());
            queryParams.Add("Phone", customer.Phone?.Trim());
            queryParams.Add("Email", customer.Email.Trim());
            queryParams.Add("Address", customer.Address?.Trim());
            queryParams.Add("City", customer.City?.Trim());
            queryParams.Add("State", customer.State?.Trim());
            queryParams.Add("Zip", customer.Zip?.Trim());
            queryParams.Add("Status", (customer.Status ?? "active").Trim().ToLowerInvariant());

            return await connection.QuerySingleOrDefaultAsync<CustomerResponseDto>(
                $@"UPDATE customer
                   SET first_name = @FirstName, last_name = @LastName, phone = @Phone, email = @Email,
                       address = @Address, city = @City, state = @State, zip = @Zip, status = @Status
                   WHERE id = @Id
                   RETURNING {CustomerColumns}",
                queryParams);
        }

        public async Task<bool> DeleteCustomer(long id)
        {
            using IDbConnection connection = _repositoryContext.GetDatabaseConnection();

            var rowsAffected = await connection.ExecuteAsync(
                "DELETE FROM customer WHERE id = @Id",
                new { Id = id });

            return rowsAffected > 0;
        }
    }
}
