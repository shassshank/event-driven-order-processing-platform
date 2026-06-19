using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using OrderService.Persistence;

namespace OrderService.IntegrationTests;

public sealed class CustomOrderServiceFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString;

    public CustomOrderServiceFactory(string connectionString) => _connectionString = connectionString;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Outbox:Enabled"] = "false",
                ["RabbitMQ:ConsumersEnabled"] = "false"
            });
        });

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<OrderDbContext>>();
            services.AddDbContext<OrderDbContext>(options => options.UseNpgsql(_connectionString));

            // Keep these API integration tests independent from RabbitMQ and the background outbox publisher.
            // WebApplicationFactory/minimal hosting can apply appsettings before the in-memory override is visible
            // to Program.cs service registration, so remove hosted services defensively as well as setting flags.
            services.RemoveAll<IHostedService>();

            // The test fixture creates the schema after the WebApplicationFactory has built the host.
            // Avoid BuildServiceProvider here so ASP.NET analyzers do not flag ASP0000.
        });
    }
}
