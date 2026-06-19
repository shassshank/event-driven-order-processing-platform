using Microsoft.EntityFrameworkCore;

namespace PaymentService.Persistence;

public sealed class PaymentDatabaseInitializer
{
    private readonly PaymentDbContext _dbContext;
    private readonly ILogger<PaymentDatabaseInitializer> _logger;

    public PaymentDatabaseInitializer(PaymentDbContext dbContext, ILogger<PaymentDatabaseInitializer> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _dbContext.Database.ExecuteSqlRawAsync("""
            create table if not exists payment_attempts (
                "Id" uuid primary key,
                "OrderId" uuid not null,
                "Amount" numeric(18, 2) not null,
                "Currency" varchar(3) not null,
                "Status" varchar(32) not null,
                "ProviderTransactionId" varchar(128) null,
                "FailureCode" varchar(64) null,
                "FailureReason" varchar(512) null,
                "CreatedAtUtc" timestamptz not null,
                "UpdatedAtUtc" timestamptz not null
            );
            create unique index if not exists "IX_payment_attempts_OrderId" on payment_attempts ("OrderId");
            create unique index if not exists "IX_payment_attempts_ProviderTransactionId" on payment_attempts ("ProviderTransactionId") where "ProviderTransactionId" is not null;

            create table if not exists payment_cancelled_orders (
                "OrderId" uuid primary key,
                "CustomerId" uuid not null,
                "Reason" varchar(512) not null,
                "Status" varchar(32) not null,
                "CancelledAtUtc" timestamptz not null,
                "CreatedAtUtc" timestamptz not null,
                "UpdatedAtUtc" timestamptz not null
            );

            create table if not exists payment_processed_messages (
                "MessageId" uuid not null,
                "EventType" varchar(256) not null,
                "ConsumerName" varchar(256) not null,
                "ProcessedAtUtc" timestamptz not null,
                constraint "PK_payment_processed_messages" primary key ("MessageId", "ConsumerName")
            );

            create table if not exists payment_outbox_messages (
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
            create unique index if not exists "IX_payment_outbox_messages_MessageId" on payment_outbox_messages ("MessageId");
            create index if not exists "IX_payment_outbox_messages_Status_OccurredOnUtc" on payment_outbox_messages ("Status", "OccurredOnUtc");
            """, cancellationToken);

        _logger.LogInformation("PaymentService database schema verified.");
    }
}
