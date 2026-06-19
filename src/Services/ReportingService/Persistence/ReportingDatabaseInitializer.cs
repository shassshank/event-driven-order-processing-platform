using Microsoft.EntityFrameworkCore;

namespace ReportingService.Persistence;

public sealed class ReportingDatabaseInitializer
{
    private readonly ReportingDbContext _dbContext;
    private readonly ILogger<ReportingDatabaseInitializer> _logger;

    public ReportingDatabaseInitializer(ReportingDbContext dbContext, ILogger<ReportingDatabaseInitializer> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _dbContext.Database.ExecuteSqlRawAsync("""
            create table if not exists reporting_orders (
                "OrderId" uuid primary key,
                "CustomerId" uuid null,
                "Status" varchar(64) not null,
                "TotalAmount" numeric(18,2) not null,
                "Currency" varchar(3) not null,
                "LastEventType" varchar(256) not null,
                "CreatedAtUtc" timestamptz null,
                "UpdatedAtUtc" timestamptz not null
            );

            create table if not exists reporting_events (
                "Id" bigserial primary key,
                "MessageId" uuid not null,
                "CorrelationId" uuid not null,
                "OrderId" uuid null,
                "EventType" varchar(256) not null,
                "Source" varchar(256) not null,
                "Payload" jsonb not null,
                "OccurredOnUtc" timestamptz not null,
                "RecordedAtUtc" timestamptz not null
            );
            create unique index if not exists "IX_reporting_events_MessageId" on reporting_events ("MessageId");
            create index if not exists "IX_reporting_events_OrderId" on reporting_events ("OrderId");
            create index if not exists "IX_reporting_events_EventType" on reporting_events ("EventType");

            create table if not exists reporting_processed_messages (
                "MessageId" uuid not null,
                "EventType" varchar(256) not null,
                "ConsumerName" varchar(256) not null,
                "ProcessedAtUtc" timestamptz not null,
                constraint "PK_reporting_processed_messages" primary key ("MessageId", "ConsumerName")
            );
            """, cancellationToken);

        _logger.LogInformation("ReportingService database schema is ready.");
    }
}
