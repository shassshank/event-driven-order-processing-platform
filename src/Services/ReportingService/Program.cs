using BuildingBlocks.EventBus.RabbitMQ;
using BuildingBlocks.Observability;
using BuildingBlocks.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using ReportingService.Messaging;
using ReportingService.Persistence;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .Enrich.WithEnvironmentName()
    .Enrich.WithThreadId()
    .WriteTo.Console());

var connectionString = builder.Configuration.GetConnectionString("ReportingDb")
    ?? throw new InvalidOperationException("ConnectionStrings:ReportingDb is required.");

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddProblemDetails();
builder.Services.AddCorrelationContext();
builder.Services.AddServiceTelemetry("ReportingService");
builder.Services.AddRabbitMqEventBus(builder.Configuration);
builder.Services.AddSingleton<ISystemClock, SystemClock>();
builder.Services.AddDbContext<ReportingDbContext>(options => options.UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory_ReportingService")));
builder.Services.AddScoped<ReportingDatabaseInitializer>();
builder.Services.AddScoped<ReportingProjectionHandler>();

var rabbitMqConsumersEnabled = builder.Configuration.GetValue("RabbitMQ:ConsumersEnabled", true);
if (rabbitMqConsumersEnabled)
{
    builder.Services.AddHostedService<RabbitMqReportingConsumer>();
}

builder.Services
    .AddHealthChecks()
    .AddNpgSql(connectionString, name: "reporting-postgres")
    .AddCheck("rabbitmq", () =>
    {
        try
        {
            var options = builder.Configuration.GetSection("RabbitMQ").Get<RabbitMqOptions>() ?? new RabbitMqOptions();
            using var connection = RabbitMqConnectionFactory.Create(options);
            return connection.IsOpen
                ? HealthCheckResult.Healthy("RabbitMQ is reachable.")
                : HealthCheckResult.Unhealthy("RabbitMQ connection is not open.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("RabbitMQ is unreachable.", ex);
        }
    });

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var initializer = scope.ServiceProvider.GetRequiredService<ReportingDatabaseInitializer>();
    await initializer.InitializeAsync(CancellationToken.None);
}

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseSerilogRequestLogging();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapControllers();
app.MapHealthChecks("/health");

app.Run();

public partial class Program;
