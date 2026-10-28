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
    public class EmployeeRepository : IEmployeeRepository
    {
        private readonly IRepositoryContext _repositoryContext;

        public EmployeeRepository(IRepositoryContext repositoryContext)
        {
            _repositoryContext = repositoryContext;
        }

        // The employee table has no salesperson flag or commission_rate column,
        // so commission is treated as the rate (percent) and a salesperson is
        // an employee with a commission above zero.
        private const string EmployeeColumns =
            "id, first_name AS First_Name, last_name AS Last_Name, department, job, salary, bonus, " +
            "commission AS Commission_Rate, status, (COALESCE(commission, 0) > 0) AS Is_Salesperson";

        public async Task<PagedList<EmployeeResponseDto>> GetAllEmployees(EmployeeParameters employeeParameters)
        {
            using IDbConnection connection = _repositoryContext.GetDatabaseConnection();

            var whereClauses = new List<string>();
            var queryParams = new DynamicParameters();

            var status = employeeParameters.Status?.Trim();
            if (!string.IsNullOrWhiteSpace(status) &&
                (status.Equals("active", StringComparison.OrdinalIgnoreCase) ||
                 status.Equals("inactive", StringComparison.OrdinalIgnoreCase)))
            {
                whereClauses.Add("LOWER(status) = @Status");
                queryParams.Add("Status", status.ToLowerInvariant());
            }

            var department = employeeParameters.Department?.Trim();
            if (!string.IsNullOrWhiteSpace(department))
            {
                whereClauses.Add("LOWER(department) = @Department");
                queryParams.Add("Department", department.ToLowerInvariant());
            }

            if (employeeParameters.IsSalesperson.HasValue)
            {
                whereClauses.Add(employeeParameters.IsSalesperson.Value
                    ? "COALESCE(commission, 0) > 0"
                    : "COALESCE(commission, 0) <= 0");
            }

            var searchTerm = employeeParameters.SearchTerm?.Trim();
            if (!string.IsNullOrWhiteSpace(searchTerm))
            {
                whereClauses.Add(@"(
                    LOWER(first_name) LIKE @SearchTerm OR
                    LOWER(last_name) LIKE @SearchTerm OR
                    LOWER(department) LIKE @SearchTerm OR
                    LOWER(job) LIKE @SearchTerm
                )");
                queryParams.Add("SearchTerm", $"%{searchTerm.ToLowerInvariant()}%");
            }

            var whereSql = whereClauses.Count > 0
                ? " WHERE " + string.Join(" AND ", whereClauses)
                : string.Empty;

            var count = await connection.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM employee{whereSql}", queryParams);

            queryParams.Add("Offset", (employeeParameters.PageNumber - 1) * employeeParameters.PageSize);
            queryParams.Add("Limit", employeeParameters.PageSize);

            var employees = (await connection.QueryAsync<EmployeeResponseDto>(
                $"SELECT {EmployeeColumns} FROM employee{whereSql} ORDER BY last_name, first_name, id OFFSET @Offset LIMIT @Limit",
                queryParams)).ToList();

            return new PagedList<EmployeeResponseDto>(
                employees,
                count,
                employeeParameters.PageNumber,
                employeeParameters.PageSize);
        }

        public async Task<EmployeeResponseDto?> GetEmployeeById(long id)
        {
            using IDbConnection connection = _repositoryContext.GetDatabaseConnection();

            return await connection.QuerySingleOrDefaultAsync<EmployeeResponseDto>(
                $"SELECT {EmployeeColumns} FROM employee WHERE id = @Id",
                new { Id = id });
        }

        public async Task<IEnumerable<EmployeeResponseDto>> GetActiveSalespeople()
        {
            using IDbConnection connection = _repositoryContext.GetDatabaseConnection();

            return await connection.QueryAsync<EmployeeResponseDto>(
                $@"SELECT {EmployeeColumns} FROM employee
                   WHERE LOWER(status) = 'active' AND COALESCE(commission, 0) > 0
                   ORDER BY last_name, first_name, id");
        }

        public async Task<EmployeeResponseDto> AddEmployee(EmployeeCreateDto employee)
        {
            using IDbConnection connection = _repositoryContext.GetDatabaseConnection();

            var queryParams = new DynamicParameters();
            queryParams.Add("FirstName", employee.First_Name.Trim());
            queryParams.Add("LastName", employee.Last_Name.Trim());
            queryParams.Add("Department", employee.Department?.Trim());
            queryParams.Add("Job", employee.Job?.Trim());
            queryParams.Add("Salary", employee.Salary);
            queryParams.Add("Bonus", employee.Bonus);
            queryParams.Add("Commission", employee.Commission_Rate);
            queryParams.Add("Status", (employee.Status ?? "active").Trim().ToLowerInvariant());

            return await connection.QuerySingleAsync<EmployeeResponseDto>(
                $@"INSERT INTO employee (first_name, last_name, department, job, salary, bonus, commission, status)
                   VALUES (@FirstName, @LastName, @Department, @Job, @Salary, @Bonus, @Commission, @Status)
                   RETURNING {EmployeeColumns}",
                queryParams);
        }

        public async Task<EmployeeResponseDto?> UpdateEmployee(long id, EmployeeUpdateDto employee)
        {
            using IDbConnection connection = _repositoryContext.GetDatabaseConnection();

            var queryParams = new DynamicParameters();
            queryParams.Add("Id", id);
            queryParams.Add("FirstName", employee.First_Name.Trim());
            queryParams.Add("LastName", employee.Last_Name.Trim());
            queryParams.Add("Department", employee.Department?.Trim());
            queryParams.Add("Job", employee.Job?.Trim());
            queryParams.Add("Salary", employee.Salary);
            queryParams.Add("Bonus", employee.Bonus);
            queryParams.Add("Commission", employee.Commission_Rate);
            queryParams.Add("Status", (employee.Status ?? "active").Trim().ToLowerInvariant());

            return await connection.QuerySingleOrDefaultAsync<EmployeeResponseDto>(
                $@"UPDATE employee
                   SET first_name = @FirstName, last_name = @LastName, department = @Department, job = @Job,
                       salary = @Salary, bonus = @Bonus, commission = @Commission, status = @Status
                   WHERE id = @Id
                   RETURNING {EmployeeColumns}",
                queryParams);
        }

        public async Task<bool> DeleteEmployee(long id)
        {
            using IDbConnection connection = _repositoryContext.GetDatabaseConnection();

            var rowsAffected = await connection.ExecuteAsync(
                "DELETE FROM employee WHERE id = @Id",
                new { Id = id });

            return rowsAffected > 0;
        }

        public async Task<bool> IsEmployeeReferenced(long id)
        {
            using IDbConnection connection = _repositoryContext.GetDatabaseConnection();

            return await connection.ExecuteScalarAsync<bool>(
                "SELECT EXISTS (SELECT 1 FROM sales_invoice WHERE salesperson_id = @Id)",
                new { Id = id });
        }
    }
}
