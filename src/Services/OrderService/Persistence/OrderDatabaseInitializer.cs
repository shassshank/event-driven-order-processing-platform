using Microsoft.EntityFrameworkCore;

namespace OrderService.Persistence;

public sealed class OrderDatabaseInitializer
{
    private readonly OrderDbContext _dbContext;
    private readonly ILogger<OrderDatabaseInitializer> _logger;

    public OrderDatabaseInitializer(OrderDbContext dbContext, ILogger<OrderDatabaseInitializer> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _dbContext.Database.ExecuteSqlRawAsync("""
            create table if not exists orders (
                "Id" uuid primary key,
                "CustomerId" uuid not null,
                "ClientRequestId" varchar(128) not null,
                "Status" varchar(40) not null,
                "TotalAmount" numeric(18, 2) not null,
                "Currency" varchar(3) not null,
                "CreatedAtUtc" timestamptz not null,
                "UpdatedAtUtc" timestamptz not null
            );
            create unique index if not exists "IX_orders_ClientRequestId" on orders ("ClientRequestId");

            create table if not exists order_items (
                "Id" uuid primary key,
                "OrderId" uuid not null references orders("Id") on delete cascade,
                "ProductId" uuid not null,
                "Quantity" integer not null,
                "UnitPrice" numeric(18, 2) not null,
                "LineTotal" numeric(18, 2) not null
            );
            create unique index if not exists "IX_order_items_OrderId_ProductId" on order_items ("OrderId", "ProductId");

            create table if not exists outbox_messages (
                "Id" bigserial primary key,
                "MessageId" uuid not null,
                "CorrelationId" uuid not null,
                "EventType" varchar(256) not null,
                "EventVersion" integer not null,
                "RoutingKey" varchar(256) not null,
                "Payload" jsonb not null,
                "OccurredOnUtc" timestamptz not null,
                "ProcessedOnUtc" timestamptz null,
                "PublishAttempts" integer not null default 0,
                "Error" varchar(4000) null,
                "Status" varchar(32) not null
            );
            create unique index if not exists "IX_outbox_messages_MessageId" on outbox_messages ("MessageId");
            create index if not exists "IX_outbox_messages_Status_OccurredOnUtc" on outbox_messages ("Status", "OccurredOnUtc");

            create table if not exists processed_messages (
                "MessageId" uuid not null,
                "EventType" varchar(256) not null,
                "ConsumerName" varchar(256) not null,
                "ProcessedAtUtc" timestamptz not null,
                constraint "PK_processed_messages" primary key ("MessageId", "ConsumerName")
            );
            """, cancellationToken);

        _logger.LogInformation("OrderService database schema verified.");
    }
}
