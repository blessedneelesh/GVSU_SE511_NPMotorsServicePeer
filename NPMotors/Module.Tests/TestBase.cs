using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using NPMotors.Repository.Context;

namespace Module.Tests
{
    [ExcludeFromCodeCoverage]
    public class TestBase : IDisposable
    {
        private readonly TestWebApplicationFactory _factory;
        public HttpClient _testClient;
        public IConfiguration _config;
        public RepositoryContext _repositoryContext;

        public TestBase()
        {
            _config = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.test.json", optional: false, reloadOnChange: true)
                .Build();

            _factory = new TestWebApplicationFactory();
            _testClient = _factory.CreateClient();
            _testClient.Timeout = TimeSpan.FromSeconds(60);

            _repositoryContext = new RepositoryContext(_config);
        }

        public void Dispose()
        {
            _testClient.Dispose();
            _factory.Dispose();
            GC.SuppressFinalize(this);
        }

        private class TestWebApplicationFactory : WebApplicationFactory<Program>
        {
            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                builder.ConfigureAppConfiguration((context, config) =>
                {
                    config.AddJsonFile(
                        Path.Combine(AppContext.BaseDirectory, "appsettings.test.json"),
                        optional: false,
                        reloadOnChange: false);
                });
            }
        }
    }
}
