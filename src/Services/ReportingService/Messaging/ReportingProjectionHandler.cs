using System.Text.Json;
using BuildingBlocks.Contracts.Events;
using BuildingBlocks.Persistence;
using BuildingBlocks.SharedKernel;
using Microsoft.EntityFrameworkCore;
using ReportingService.Domain;
using ReportingService.Persistence;

namespace ReportingService.Messaging;

public sealed class ReportingProjectionHandler
{
    public const string ConsumerName = "ReportingService.AllEvents";

    private readonly ReportingDbContext _dbContext;
    private readonly ISystemClock _clock;
    private readonly ILogger<ReportingProjectionHandler> _logger;

    public ReportingProjectionHandler(ReportingDbContext dbContext, ISystemClock clock, ILogger<ReportingProjectionHandler> logger)
    {
        _dbContext = dbContext;
        _clock = clock;
        _logger = logger;
    }

    public async Task<ReportingProjectionResult> HandleAsync(
        Guid messageId,
        Guid correlationId,
        string eventType,
        string source,
        DateTime occurredOnUtc,
        string payloadJson,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var alreadyProcessed = await _dbContext.ProcessedMessages.AnyAsync(
            message => message.MessageId == messageId && message.ConsumerName == ConsumerName,
            cancellationToken);

        if (alreadyProcessed)
        {
            return ReportingProjectionResult.Duplicate(messageId);
        }

        var orderId = TryReadOrderId(payload);
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        _dbContext.Events.Add(ReportingEvent.Create(
            messageId,
            correlationId,
            orderId,
            eventType,
            source,
            payloadJson,
            occurredOnUtc,
            _clock.UtcNow));

        if (orderId is not null)
        {
            var order = await _dbContext.Orders.SingleOrDefaultAsync(existing => existing.OrderId == orderId.Value, cancellationToken)
                ?? ReportingOrder.Create(orderId.Value, _clock.UtcNow);

            if (_dbContext.Entry(order).State == EntityState.Detached)
            {
                _dbContext.Orders.Add(order);
            }

            ApplyProjection(order, eventType, payload, occurredOnUtc);
        }

        _dbContext.ProcessedMessages.Add(new ProcessedMessage
        {
            MessageId = messageId,
            EventType = eventType,
            ConsumerName = ConsumerName,
            ProcessedAtUtc = _clock.UtcNow
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        _logger.LogInformation("Projected event {EventType} with message id {MessageId} for order {OrderId}.", eventType, messageId, orderId);
        return ReportingProjectionResult.Projected(messageId, orderId);
    }

    private static void ApplyProjection(ReportingOrder order, string eventType, JsonElement payload, DateTime occurredOnUtc)
    {
        switch (eventType)
        {
            case nameof(OrderCreated):
                order.ApplyOrderCreated(
                    ReadGuid(payload, "customerId"),
                    ReadDecimal(payload, "totalAmount"),
                    ReadString(payload, "currency"),
                    occurredOnUtc);
                break;
            case nameof(InventoryReserved):
                order.ApplyInventoryReserved(occurredOnUtc);
                break;
            case nameof(InventoryReservationFailed):
                order.ApplyInventoryFailed(occurredOnUtc);
                break;
            case nameof(PaymentCompleted):
                order.ApplyPaymentCompleted(ReadDecimal(payload, "amount"), ReadString(payload, "currency"), occurredOnUtc);
                break;
            case nameof(PaymentFailed):
                order.ApplyPaymentFailed(occurredOnUtc);
                break;
            case nameof(PaymentRefundRequired):
                order.ApplyPaymentRefundRequired(occurredOnUtc);
                break;
            case nameof(PaymentDuplicateRejected):
                order.ApplyPaymentDuplicateRejected(occurredOnUtc);
                break;
            case nameof(OrderCompleted):
                order.ApplyOrderCompleted(ReadDecimal(payload, "totalAmount"), ReadString(payload, "currency"), occurredOnUtc);
                break;
            case nameof(OrderCancelled):
                order.ApplyOrderCancelled(occurredOnUtc);
                break;
            case nameof(InventoryReleased):
                order.ApplyInventoryReleased(occurredOnUtc);
                break;
        }
    }

    private static Guid? TryReadOrderId(JsonElement payload)
    {
        return payload.TryGetProperty("orderId", out var property) && property.TryGetGuid(out var orderId)
            ? orderId
            : null;
    }

    private static Guid ReadGuid(JsonElement payload, string propertyName)
    {
        if (!payload.TryGetProperty(propertyName, out var property) || !property.TryGetGuid(out var value))
        {
            throw new JsonException($"Reporting payload does not contain a valid {propertyName}.");
        }

        return value;
    }

    private static decimal ReadDecimal(JsonElement payload, string propertyName)
    {
        if (!payload.TryGetProperty(propertyName, out var property) || !property.TryGetDecimal(out var value))
        {
            throw new JsonException($"Reporting payload does not contain a valid {propertyName}.");
        }

        return value;
    }

    private static string ReadString(JsonElement payload, string propertyName)
    {
        return payload.TryGetProperty(propertyName, out var property) && property.GetString() is { Length: > 0 } value
            ? value
            : throw new JsonException($"Reporting payload does not contain a valid {propertyName}.");
    }
}

public enum ReportingProjectionOutcome
{
    Projected = 0,
    Duplicate = 1
}

public sealed record ReportingProjectionResult(Guid MessageId, ReportingProjectionOutcome Outcome, Guid? OrderId = null)
{
    public static ReportingProjectionResult Projected(Guid messageId, Guid? orderId) => new(messageId, ReportingProjectionOutcome.Projected, orderId);
    public static ReportingProjectionResult Duplicate(Guid messageId) => new(messageId, ReportingProjectionOutcome.Duplicate);
}
