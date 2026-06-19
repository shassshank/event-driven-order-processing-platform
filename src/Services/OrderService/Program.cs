using BuildingBlocks.EventBus.RabbitMQ;
using OrderService.Caching;
using BuildingBlocks.Observability;
using BuildingBlocks.SharedKernel;
using FluentValidation;
using FluentValidation.AspNetCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OrderService.Features.Orders;
using OrderService.Messaging;
using OrderService.Outbox;
using OrderService.Persistence;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .Enrich.WithEnvironmentName()
    .Enrich.WithThreadId()
    .WriteTo.Console());

var connectionString = builder.Configuration.GetConnectionString("OrdersDb")
    ?? throw new InvalidOperationException("ConnectionStrings:OrdersDb is required.");
var redisConnectionString = builder.Configuration.GetConnectionString("Redis") ?? "localhost:6379";

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddProblemDetails();
builder.Services.AddCorrelationContext();
builder.Services.AddServiceTelemetry(OrderServiceDiagnostics.ServiceName);
builder.Services.AddRabbitMqEventBus(builder.Configuration);
builder.Services.Configure<OutboxOptions>(builder.Configuration.GetSection("Outbox"));
builder.Services.Configure<RedisOrderCacheOptions>(builder.Configuration.GetSection("OrderCache"));
builder.Services.AddStackExchangeRedisCache(options => options.Configuration = redisConnectionString);
builder.Services.AddScoped<IOrderCache, RedisOrderCache>();
builder.Services.AddScoped<OutboxPublisher>();
builder.Services.AddScoped<InventoryEventHandler>();
builder.Services.AddScoped<PaymentEventHandler>();
builder.Services.AddSingleton<ISystemClock, SystemClock>();
builder.Services.AddScoped<IOrderApplicationService, OrderApplicationService>();
builder.Services.AddValidatorsFromAssemblyContaining<CreateOrderRequestValidator>();
builder.Services.AddFluentValidationAutoValidation();

builder.Services.AddDbContext<OrderDbContext>(options => options.UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory_OrderService")));
builder.Services.AddScoped<OrderDatabaseInitializer>();

var outboxEnabled = builder.Configuration.GetValue("Outbox:Enabled", true);
if (outboxEnabled)
{
    builder.Services.AddHostedService<OutboxPublisherBackgroundService>();
}

var rabbitMqConsumersEnabled = builder.Configuration.GetValue("RabbitMQ:ConsumersEnabled", true);
if (rabbitMqConsumersEnabled)
{
    builder.Services.AddHostedService<RabbitMqInventoryEventsConsumer>();
    builder.Services.AddHostedService<RabbitMqPaymentEventsConsumer>();
}

builder.Services
    .AddHealthChecks()
    .AddNpgSql(connectionString, name: "orders-postgres")
    .AddRedis(redisConnectionString, name: "redis")
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

builder.Services.Configure<ApiBehaviorOptions>(options =>
{
    options.InvalidModelStateResponseFactory = context =>
    {
        var problemDetails = new ValidationProblemDetails(context.ModelState)
        {
            Title = "The request payload is invalid.",
            Status = StatusCodes.Status400BadRequest,
            Type = "https://httpstatuses.com/400"
        };

        return new BadRequestObjectResult(problemDetails);
    };
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var initializer = scope.ServiceProvider.GetRequiredService<OrderDatabaseInitializer>();
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
