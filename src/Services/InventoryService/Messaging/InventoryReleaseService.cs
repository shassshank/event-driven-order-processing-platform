using System.Text.Json;
using BuildingBlocks.Contracts;
using BuildingBlocks.Contracts.Events;
using BuildingBlocks.Persistence;
using BuildingBlocks.SharedKernel;
using InventoryService.Domain;
using InventoryService.Persistence;
using Microsoft.EntityFrameworkCore;

namespace InventoryService.Messaging;

public sealed class InventoryReleaseService
{
    public const string PaymentFailedConsumerName = "InventoryService.PaymentFailed";
    public const string ConsumerName = PaymentFailedConsumerName;
    public const string OrderCancelledConsumerName = "InventoryService.OrderCancelled";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly InventoryDbContext _dbContext;
    private readonly ISystemClock _clock;
    private readonly ILogger<InventoryReleaseService> _logger;

    public InventoryReleaseService(
        InventoryDbContext dbContext,
        ISystemClock clock,
        ILogger<InventoryReleaseService> logger)
    {
        _dbContext = dbContext;
        _clock = clock;
        _logger = logger;
    }

    public Task<InventoryReleaseResult> HandlePaymentFailedAsync(
        EventEnvelope<PaymentFailed> envelope,
        CancellationToken cancellationToken) =>
        ReleaseReservationsAsync(
            envelope.MessageId,
            envelope.EventType,
            envelope.CorrelationId,
            envelope.MessageId,
            envelope.Payload.OrderId,
            PaymentFailedConsumerName,
            "PaymentFailed",
            cancelledCustomerId: null,
            cancellationReason: null,
            cancellationToken: cancellationToken);

    public Task<InventoryReleaseResult> HandleOrderCancelledAsync(
        EventEnvelope<OrderCancelled> envelope,
        CancellationToken cancellationToken) =>
        ReleaseReservationsAsync(
            envelope.MessageId,
            envelope.EventType,
            envelope.CorrelationId,
            envelope.MessageId,
            envelope.Payload.OrderId,
            OrderCancelledConsumerName,
            "OrderCancelled",
            envelope.Payload.CustomerId,
            envelope.Payload.Reason,
            cancellationToken);

    private async Task<InventoryReleaseResult> ReleaseReservationsAsync(
        Guid messageId,
        string eventType,
        Guid correlationId,
        Guid causationId,
        Guid orderId,
        string consumerName,
        string releaseReason,
        Guid? cancelledCustomerId,
        string? cancellationReason,
        CancellationToken cancellationToken)
    {
        var alreadyProcessed = await _dbContext.ProcessedMessages.AnyAsync(
            message => message.MessageId == messageId && message.ConsumerName == consumerName,
            cancellationToken);

        if (alreadyProcessed)
        {
            return InventoryReleaseResult.Duplicate(orderId);
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        if (cancelledCustomerId.HasValue)
        {
            var cancellationAlreadyRecorded = await _dbContext.CancelledOrders.AnyAsync(
                cancelled => cancelled.OrderId == orderId,
                cancellationToken);

            if (!cancellationAlreadyRecorded)
            {
                _dbContext.CancelledOrders.Add(CancelledOrder.Create(
                    orderId,
                    cancelledCustomerId.Value,
                    cancellationReason ?? string.Empty,
                    messageId,
                    _clock.UtcNow));
            }
        }

        var reservations = await _dbContext.Reservations
            .Where(reservation => reservation.OrderId == orderId)
            .ToListAsync(cancellationToken);

        if (reservations.Count == 0)
        {
            AddProcessedMessage(messageId, eventType, consumerName);
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            _logger.LogWarning(
                "{ReleaseReason} message {MessageId} references order {OrderId} with no inventory reservations.",
                releaseReason,
                messageId,
                orderId);

            return InventoryReleaseResult.UnknownOrder(orderId);
        }

        var releasedItems = new List<InventoryReleasedItem>();
        foreach (var reservation in reservations.Where(reservation => reservation.Status == InventoryReservationStatus.Reserved))
        {
            var product = await _dbContext.Products.SingleAsync(product => product.Id == reservation.ProductId, cancellationToken);
            product.Release(reservation.Quantity, _clock.UtcNow);
            reservation.Release();
            releasedItems.Add(new InventoryReleasedItem(reservation.ProductId, reservation.Quantity));
        }

        AddProcessedMessage(messageId, eventType, consumerName);
        if (releasedItems.Count > 0)
        {
            AddInventoryReleasedOutboxMessage(correlationId, causationId, orderId, releasedItems);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        _logger.LogInformation(
            "Released {ReleasedItemCount} inventory reservations for order {OrderId} due to {ReleaseReason} message {MessageId}.",
            releasedItems.Count,
            orderId,
            releaseReason,
            messageId);

        return releasedItems.Count > 0
            ? InventoryReleaseResult.Released(orderId)
            : InventoryReleaseResult.NoOp(orderId);
    }

    private void AddProcessedMessage(Guid messageId, string eventType, string consumerName)
    {
        _dbContext.ProcessedMessages.Add(new ProcessedMessage
        {
            MessageId = messageId,
            EventType = eventType,
            ConsumerName = consumerName,
            ProcessedAtUtc = _clock.UtcNow
        });
    }

    private void AddInventoryReleasedOutboxMessage(
        Guid correlationId,
        Guid causationId,
        Guid orderId,
        IReadOnlyCollection<InventoryReleasedItem> releasedItems)
    {
        var payload = new InventoryReleased(orderId, releasedItems.ToArray());
        var envelope = EventEnvelope.Create(
            payload,
            correlationId,
            causationId,
            "InventoryService",
            _clock.UtcNow);

        _dbContext.OutboxMessages.Add(OutboxMessage.Create(
            envelope.MessageId,
            envelope.CorrelationId,
            envelope.EventType,
            envelope.EventVersion,
            EventRoutingKeys.InventoryReleased,
            JsonSerializer.Serialize(envelope, JsonOptions),
            envelope.OccurredOnUtc));
    }
}

public enum InventoryReleaseOutcome
{
    Released = 0,
    Duplicate = 1,
    UnknownOrder = 2,
    NoOp = 3
}

public sealed record InventoryReleaseResult(Guid OrderId, InventoryReleaseOutcome Outcome)
{
    public static InventoryReleaseResult Released(Guid orderId) => new(orderId, InventoryReleaseOutcome.Released);
    public static InventoryReleaseResult Duplicate(Guid orderId) => new(orderId, InventoryReleaseOutcome.Duplicate);
    public static InventoryReleaseResult UnknownOrder(Guid orderId) => new(orderId, InventoryReleaseOutcome.UnknownOrder);
    public static InventoryReleaseResult NoOp(Guid orderId) => new(orderId, InventoryReleaseOutcome.NoOp);
}
