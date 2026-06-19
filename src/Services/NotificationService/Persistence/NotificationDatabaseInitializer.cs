using Microsoft.EntityFrameworkCore;

namespace NotificationService.Persistence;

public sealed class NotificationDatabaseInitializer
{
    private readonly NotificationDbContext _dbContext;
    private readonly ILogger<NotificationDatabaseInitializer> _logger;

    public NotificationDatabaseInitializer(NotificationDbContext dbContext, ILogger<NotificationDatabaseInitializer> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _dbContext.Database.ExecuteSqlRawAsync("""
            create table if not exists notification_messages (
                "Id" bigserial primary key,
                "MessageId" uuid not null,
                "OrderId" uuid not null,
                "TriggerEventType" varchar(256) not null,
                "Channel" varchar(64) not null,
                "Recipient" varchar(256) not null,
                "Template" varchar(128) not null,
                "Status" varchar(32) not null,
                "Error" varchar(4000) null,
                "CreatedAtUtc" timestamptz not null
            );
            create unique index if not exists "IX_notification_messages_MessageId" on notification_messages ("MessageId");
            create index if not exists "IX_notification_messages_OrderId" on notification_messages ("OrderId");

            create table if not exists notification_processed_messages (
                "MessageId" uuid not null,
                "EventType" varchar(256) not null,
                "ConsumerName" varchar(256) not null,
                "ProcessedAtUtc" timestamptz not null,
                constraint "PK_notification_processed_messages" primary key ("MessageId", "ConsumerName")
            );

            create table if not exists notification_outbox_messages (
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
            create unique index if not exists "IX_notification_outbox_messages_MessageId" on notification_outbox_messages ("MessageId");
            create index if not exists "IX_notification_outbox_messages_Status_OccurredOnUtc" on notification_outbox_messages ("Status", "OccurredOnUtc");
            """, cancellationToken);

        _logger.LogInformation("NotificationService database schema is ready.");
    }
}
