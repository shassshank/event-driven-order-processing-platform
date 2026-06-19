using Microsoft.EntityFrameworkCore;

namespace InventoryService.Persistence;

public sealed class InventoryDatabaseInitializer
{
    public static readonly Guid DemoProductId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private readonly InventoryDbContext _dbContext;
    private readonly ILogger<InventoryDatabaseInitializer> _logger;

    public InventoryDatabaseInitializer(InventoryDbContext dbContext, ILogger<InventoryDatabaseInitializer> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _dbContext.Database.ExecuteSqlRawAsync("""
            create table if not exists inventory_products (
                "Id" uuid primary key,
                "Sku" varchar(64) not null,
                "Name" varchar(256) not null,
                "AvailableStock" integer not null,
                "ReservedStock" integer not null,
                "UpdatedAtUtc" timestamptz not null,
                "ConcurrencyToken" uuid not null
            );
            create unique index if not exists "IX_inventory_products_Sku" on inventory_products ("Sku");

            create table if not exists inventory_reservations (
                "OrderId" uuid not null,
                "ProductId" uuid not null,
                "Quantity" integer not null,
                "Status" varchar(32) not null,
                "CreatedAtUtc" timestamptz not null,
                constraint "PK_inventory_reservations" primary key ("OrderId", "ProductId")
            );


            create table if not exists inventory_cancelled_orders (
                "OrderId" uuid primary key,
                "CustomerId" uuid not null,
                "Reason" varchar(512) not null,
                "MessageId" uuid not null,
                "CancelledAtUtc" timestamptz not null
            );
            create unique index if not exists "IX_inventory_cancelled_orders_MessageId" on inventory_cancelled_orders ("MessageId");

            create table if not exists inventory_processed_messages (
                "MessageId" uuid not null,
                "EventType" varchar(256) not null,
                "ConsumerName" varchar(256) not null,
                "ProcessedAtUtc" timestamptz not null,
                constraint "PK_inventory_processed_messages" primary key ("MessageId", "ConsumerName")
            );

            create table if not exists inventory_outbox_messages (
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
            create unique index if not exists "IX_inventory_outbox_messages_MessageId" on inventory_outbox_messages ("MessageId");
            create index if not exists "IX_inventory_outbox_messages_Status_OccurredOnUtc" on inventory_outbox_messages ("Status", "OccurredOnUtc");
            """, cancellationToken);

        var exists = await _dbContext.Products.AnyAsync(product => product.Id == DemoProductId, cancellationToken);
        if (!exists)
        {
            var seedConcurrencyToken = Guid.Parse("44444444-4444-4444-4444-444444444444");
            await _dbContext.Database.ExecuteSqlAsync($"""
                insert into inventory_products
                    ("Id", "Sku", "Name", "AvailableStock", "ReservedStock", "UpdatedAtUtc", "ConcurrencyToken")
                values
                    ({DemoProductId}, 'DEMO-SKU-001', 'Demo Portfolio Product', 100, 0, now() at time zone 'utc', {seedConcurrencyToken});
                """, cancellationToken);

            _logger.LogInformation("Seeded demo product {ProductId} with stock 100.", DemoProductId);
        }
    }
}
