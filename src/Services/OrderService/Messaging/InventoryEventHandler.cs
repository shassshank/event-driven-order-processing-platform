using BuildingBlocks.Contracts;
using BuildingBlocks.Contracts.Events;
using BuildingBlocks.Persistence;
using BuildingBlocks.SharedKernel;
using Microsoft.EntityFrameworkCore;
using OrderService.Caching;
using OrderService.Domain;
using OrderService.Persistence;

namespace OrderService.Messaging;

public sealed class InventoryEventHandler
{
    public const string ConsumerName = "OrderService.InventoryEvents";

    private readonly OrderDbContext _dbContext;
    private readonly ISystemClock _clock;
    private readonly IOrderCache _orderCache;
    private readonly ILogger<InventoryEventHandler> _logger;

    public InventoryEventHandler(OrderDbContext dbContext, ISystemClock clock, IOrderCache orderCache, ILogger<InventoryEventHandler> logger)
    {
        _dbContext = dbContext;
        _clock = clock;
        _orderCache = orderCache;
        _logger = logger;
    }

    public Task<InventoryEventHandlingResult> HandleInventoryReservedAsync(
        EventEnvelope<InventoryReserved> envelope,
        CancellationToken cancellationToken) =>
        HandleAsync(envelope.MessageId, envelope.EventType, envelope.Payload.OrderId, OrderStatus.InventoryReserved, cancellationToken);

    public Task<InventoryEventHandlingResult> HandleInventoryReservationFailedAsync(
        EventEnvelope<InventoryReservationFailed> envelope,
        CancellationToken cancellationToken) =>
        HandleAsync(envelope.MessageId, envelope.EventType, envelope.Payload.OrderId, OrderStatus.InventoryFailed, cancellationToken);

    private async Task<InventoryEventHandlingResult> HandleAsync(
        Guid messageId,
        string eventType,
        Guid orderId,
        OrderStatus targetStatus,
        CancellationToken cancellationToken)
    {
        var alreadyProcessed = await _dbContext.ProcessedMessages.AnyAsync(
            message => message.MessageId == messageId && message.ConsumerName == ConsumerName,
            cancellationToken);

        if (alreadyProcessed)
        {
            _logger.LogInformation(
                "Skipping duplicate inventory event {MessageId} for order {OrderId}",
                messageId,
                orderId);

            return InventoryEventHandlingResult.Duplicate(orderId);
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        var order = await _dbContext.Orders.SingleOrDefaultAsync(order => order.Id == orderId, cancellationToken);
        if (order is null)
        {
            _logger.LogWarning(
                "Inventory event {MessageId} references unknown order {OrderId}; marking message processed to avoid poison retries.",
                messageId,
                orderId);

            AddProcessedMessage(messageId, eventType);
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return InventoryEventHandlingResult.UnknownOrder(orderId);
        }

        if (order.Status == targetStatus)
        {
            AddProcessedMessage(messageId, eventType);
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return InventoryEventHandlingResult.NoOp(orderId);
        }

        var transition = order.ApplyStatusTransition(targetStatus, _clock.UtcNow);
        if (transition.IsFailure)
        {
            _logger.LogWarning(
                "Ignoring inventory event {MessageId} for order {OrderId}. Current status {CurrentStatus} cannot transition to {TargetStatus}. Error={ErrorCode}",
                messageId,
                orderId,
                order.Status,
                targetStatus,
                transition.Error.Code);

            AddProcessedMessage(messageId, eventType);
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return InventoryEventHandlingResult.InvalidTransition(orderId);
        }

        AddProcessedMessage(messageId, eventType);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await _orderCache.RemoveAsync(orderId, cancellationToken);

        _logger.LogInformation(
            "Applied inventory event {MessageId} to order {OrderId}; new status is {OrderStatus}",
            messageId,
            orderId,
            targetStatus);

        return InventoryEventHandlingResult.Updated(orderId);
    }

    private void AddProcessedMessage(Guid messageId, string eventType)
    {
        _dbContext.ProcessedMessages.Add(new ProcessedMessage
        {
            MessageId = messageId,
            EventType = eventType,
            ConsumerName = ConsumerName,
            ProcessedAtUtc = _clock.UtcNow
        });
    }
}

public enum InventoryEventHandlingOutcome
{
    Updated = 0,
    Duplicate = 1,
    UnknownOrder = 2,
    InvalidTransition = 3,
    NoOp = 4
}

public sealed record InventoryEventHandlingResult(Guid OrderId, InventoryEventHandlingOutcome Outcome)
{
    public static InventoryEventHandlingResult Updated(Guid orderId) => new(orderId, InventoryEventHandlingOutcome.Updated);
    public static InventoryEventHandlingResult Duplicate(Guid orderId) => new(orderId, InventoryEventHandlingOutcome.Duplicate);
    public static InventoryEventHandlingResult UnknownOrder(Guid orderId) => new(orderId, InventoryEventHandlingOutcome.UnknownOrder);
    public static InventoryEventHandlingResult InvalidTransition(Guid orderId) => new(orderId, InventoryEventHandlingOutcome.InvalidTransition);
    public static InventoryEventHandlingResult NoOp(Guid orderId) => new(orderId, InventoryEventHandlingOutcome.NoOp);
}
