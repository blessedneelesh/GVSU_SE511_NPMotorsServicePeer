using NPMotors.Repository.Context;
using NPMotors.Repository.Interface;
using NPMotors.Repository.Repositories;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Text;

namespace NPMotors.Repository
{
    public static class RepositoryServiceCollectionExtensions
    {
        public static IServiceCollection AddRepositoryServices(this IServiceCollection services)
        {
            services.AddScoped<IRepositoryContext, RepositoryContext>();
            services.AddScoped<ICustomerRepository, CustomerRepository>();
            services.AddScoped<IEmployeeRepository, EmployeeRepository>();
            services.AddScoped<IVendorRepository, VendorRepository>();

            return services;
        }
    }
}
