using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Text;
using NPMotors.Services.Abstractions.ServiceInterfaces;
using NPMotors.Services.Service;

namespace NPMotors.Services
{
    public static class ServicesServiceCollectionExtensions
    {
        public static IServiceCollection AddServices(this IServiceCollection services)
        {
            services.AddScoped<ICustomerService, CustomerService>();
            services.AddScoped<IEmployeeService, EmployeeService>();
            services.AddScoped<IVendorService, VendorService>();
            services.AddScoped<IBankAccountService, BankAccountService>();
            services.AddScoped<IVehicleService, VehicleService>();
            services.AddScoped<IPartService, PartService>();
            services.AddScoped<ISalesInvoiceService, SalesInvoiceService>();
            services.AddScoped<ITransactionService, TransactionService>();
            services.AddScoped<ITransactionPostingService, TransactionPostingService>();
            services.AddScoped<ITransferService, TransferService>();
            services.AddScoped<IStatementLineService, StatementLineService>();

            return services;
        }
    }
}
