using BuildingBlocks.EventBus.RabbitMQ;
using BuildingBlocks.Observability;
using BuildingBlocks.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NotificationService.Messaging;
using NotificationService.Outbox;
using NotificationService.Persistence;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .Enrich.WithEnvironmentName()
    .Enrich.WithThreadId()
    .WriteTo.Console());

var connectionString = builder.Configuration.GetConnectionString("NotificationsDb")
    ?? throw new InvalidOperationException("ConnectionStrings:NotificationsDb is required.");

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddProblemDetails();
builder.Services.AddCorrelationContext();
builder.Services.AddServiceTelemetry("NotificationService");
builder.Services.AddRabbitMqEventBus(builder.Configuration);
builder.Services.AddSingleton<ISystemClock, SystemClock>();
builder.Services.AddDbContext<NotificationDbContext>(options => options.UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory_NotificationService")));
builder.Services.AddScoped<NotificationDatabaseInitializer>();
builder.Services.AddScoped<NotificationEventHandler>();
builder.Services.Configure<OutboxOptions>(builder.Configuration.GetSection("Outbox"));
builder.Services.AddScoped<OutboxPublisher>();

var rabbitMqConsumersEnabled = builder.Configuration.GetValue("RabbitMQ:ConsumersEnabled", true);
if (rabbitMqConsumersEnabled)
{
    builder.Services.AddHostedService<RabbitMqNotificationEventsConsumer>();
}

var outboxEnabled = builder.Configuration.GetValue("Outbox:Enabled", true);
if (outboxEnabled)
{
    builder.Services.AddHostedService<OutboxPublisherBackgroundService>();
}

builder.Services
    .AddHealthChecks()
    .AddNpgSql(connectionString, name: "notifications-postgres")
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
    var initializer = scope.ServiceProvider.GetRequiredService<NotificationDatabaseInitializer>();
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
