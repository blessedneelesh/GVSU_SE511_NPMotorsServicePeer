using NPMotors.Repository.Interface;
using Npgsql;
using System;
using System.Collections.Generic;
using System.Data;
using System.Text;
using Microsoft.Extensions.Configuration;


namespace NPMotors.Repository.Context
{
    public class RepositoryContext: IRepositoryContext
    {
        private string _connectionString;
        private NpgsqlDataSource _dataSource;

        public RepositoryContext(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("NPMotorsDatabase")
                ?? throw new InvalidOperationException("Connection string 'NPMotorsDatabase' database was not found.");

            _dataSource = new NpgsqlDataSourceBuilder(_connectionString).Build();
        }

        public IDbConnection GetDatabaseConnection()
        {
            try
            {
                return OpenConnection();
            }
            catch (PostgresException ex) when (ex.SqlState == "28P01") // SQL login failure error number
            {
                // Log.Error(ex, "Database login failed (SqlState: {SqlState}). Attempting to reload secrets and retry connection.", ex.SqlState);
                return OpenConnection();
            }
            //catch (Exception ex)
            //{
            //    if (ex is PostgresException pgEx)
            //        Log.Error(pgEx, "Unexpected database exception (SqlState: {SqlState}) while opening the connection.", pgEx.SqlState);
            //    else
            //        Log.Error(ex, "An unexpected exception occurred while opening the connection.");
            //    throw new InvalidOperationException("Failed to open a database connection.", ex);
            //}
        }

        public NpgsqlConnection OpenConnection()
        {
            var connection = new NpgsqlConnection(_connectionString);
            connection.Open();
            return connection;
        }
    }
}
