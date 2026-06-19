using System.Text.Json;
using BuildingBlocks.EventBus.RabbitMQ;
using BuildingBlocks.Persistence;
using BuildingBlocks.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using InventoryService.Persistence;

namespace InventoryService.Outbox;

public sealed class OutboxPublisher
{
    private readonly InventoryDbContext _dbContext;
    private readonly IEventBus _eventBus;
    private readonly ISystemClock _clock;
    private readonly OutboxOptions _options;
    private readonly ILogger<OutboxPublisher> _logger;

    public OutboxPublisher(
        InventoryDbContext dbContext,
        IEventBus eventBus,
        ISystemClock clock,
        IOptions<OutboxOptions> options,
        ILogger<OutboxPublisher> logger)
    {
        _dbContext = dbContext;
        _eventBus = eventBus;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<int> PublishPendingMessagesAsync(CancellationToken cancellationToken)
    {
        var claimedMessageIds = await ClaimNextBatchAsync(cancellationToken);
        if (claimedMessageIds.Count == 0)
        {
            return 0;
        }

        var messages = await _dbContext.OutboxMessages
            .Where(message => claimedMessageIds.Contains(message.Id))
            .OrderBy(message => message.OccurredOnUtc)
            .ToListAsync(cancellationToken);

        var published = 0;
        foreach (var message in messages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                ValidateEnvelopeJson(message);
                await _eventBus.PublishRawAsync(
                    message.Payload,
                    message.RoutingKey,
                    message.MessageId,
                    message.CorrelationId,
                    message.EventType,
                    message.EventVersion,
                    message.OccurredOnUtc,
                    cancellationToken,
                    TryReadCausationId(message.Payload));

                message.MarkPublished(_clock.UtcNow);
                published++;
                _logger.LogInformation(
                    "Published inventory outbox message {OutboxMessageId} with message id {MessageId} after {PublishAttempts} failed attempts.",
                    message.Id,
                    message.MessageId,
                    message.PublishAttempts);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                message.MarkFailed(ex.Message);
                _logger.LogError(
                    ex,
                    "Failed to publish inventory outbox message {OutboxMessageId} with message id {MessageId}. Attempt {PublishAttempts}/{MaxPublishAttempts}.",
                    message.Id,
                    message.MessageId,
                    message.PublishAttempts,
                    _options.MaxPublishAttempts);
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return published;
    }

    private async Task<IReadOnlyList<long>> ClaimNextBatchAsync(CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var staleBefore = now.AddSeconds(-_options.ProcessingTimeoutSeconds);

        if (!_dbContext.Database.IsRelational())
        {
            var messages = await _dbContext.OutboxMessages
                .Where(message =>
                    message.Status == OutboxMessageStatus.Pending
                    || (message.Status == OutboxMessageStatus.Failed && message.PublishAttempts < _options.MaxPublishAttempts)
                    || (message.Status == OutboxMessageStatus.Processing && message.ProcessedOnUtc < staleBefore))
                .OrderBy(message => message.OccurredOnUtc)
                .Take(_options.BatchSize)
                .ToListAsync(cancellationToken);

            foreach (var message in messages)
            {
                message.MarkProcessing(now);
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
            return messages.Select(message => message.Id).ToList();
        }

        return await _dbContext.Database.SqlQuery<long>($"""
            update inventory_outbox_messages
            set "Status" = 'Processing',
                "ProcessedOnUtc" = {now},
                "Error" = null
            where "Id" in (
                select "Id"
                from inventory_outbox_messages
                where
                    "Status" = 'Pending'
                    or ("Status" = 'Failed' and "PublishAttempts" < {_options.MaxPublishAttempts})
                    or ("Status" = 'Processing' and "ProcessedOnUtc" < {staleBefore})
                order by "OccurredOnUtc"
                for update skip locked
                limit {_options.BatchSize}
            )
            returning "Id" as "Value"
            """).ToListAsync(cancellationToken);
    }

    private static void ValidateEnvelopeJson(OutboxMessage message)
    {
        using var document = JsonDocument.Parse(message.Payload);
        if (!document.RootElement.TryGetProperty("messageId", out var messageId) || !messageId.TryGetGuid(out var parsedMessageId))
        {
            throw new JsonException($"Outbox message {message.Id} payload does not contain a valid messageId.");
        }

        if (parsedMessageId != message.MessageId)
        {
            throw new JsonException($"Outbox message {message.Id} payload messageId does not match database MessageId.");
        }
    }

    private static Guid? TryReadCausationId(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.TryGetProperty("causationId", out var causationId) && causationId.TryGetGuid(out var value))
            {
                return value;
            }
        }
        catch (JsonException)
        {
            // The validation step will mark invalid payloads as failed before publishing.
        }

        return null;
    }
}
