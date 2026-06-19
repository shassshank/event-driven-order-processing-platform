using System.Text.Json;
using BuildingBlocks.Contracts;
using BuildingBlocks.Contracts.Events;
using BuildingBlocks.Persistence;
using BuildingBlocks.SharedKernel;
using Microsoft.EntityFrameworkCore;
using OrderService.Caching;
using OrderService.Domain;
using OrderService.Persistence;

namespace OrderService.Messaging;

public sealed class PaymentEventHandler
{
    public const string ConsumerName = "OrderService.PaymentEvents";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly OrderDbContext _dbContext;
    private readonly ISystemClock _clock;
    private readonly IOrderCache _orderCache;
    private readonly ILogger<PaymentEventHandler> _logger;

    public PaymentEventHandler(
        OrderDbContext dbContext,
        ISystemClock clock,
        IOrderCache orderCache,
        ILogger<PaymentEventHandler> logger)
    {
        _dbContext = dbContext;
        _clock = clock;
        _orderCache = orderCache;
        _logger = logger;
    }

    public Task<PaymentEventHandlingResult> HandlePaymentCompletedAsync(
        EventEnvelope<PaymentCompleted> envelope,
        CancellationToken cancellationToken) =>
        HandleCompletedAsync(envelope, cancellationToken);

    public Task<PaymentEventHandlingResult> HandlePaymentFailedAsync(
        EventEnvelope<PaymentFailed> envelope,
        CancellationToken cancellationToken) =>
        HandleFailedAsync(envelope, cancellationToken);

    private async Task<PaymentEventHandlingResult> HandleCompletedAsync(
        EventEnvelope<PaymentCompleted> envelope,
        CancellationToken cancellationToken)
    {
        var alreadyProcessed = await IsProcessedAsync(envelope.MessageId, cancellationToken);
        if (alreadyProcessed)
        {
            return PaymentEventHandlingResult.Duplicate(envelope.Payload.OrderId);
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        var order = await _dbContext.Orders.SingleOrDefaultAsync(order => order.Id == envelope.Payload.OrderId, cancellationToken);
        if (order is null)
        {
            AddProcessedMessage(envelope.MessageId, envelope.EventType);
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            _logger.LogWarning("PaymentCompleted {MessageId} references unknown order {OrderId}; marked processed.", envelope.MessageId, envelope.Payload.OrderId);
            return PaymentEventHandlingResult.UnknownOrder(envelope.Payload.OrderId);
        }

        if (order.Status == OrderStatus.Completed)
        {
            AddProcessedMessage(envelope.MessageId, envelope.EventType);
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return PaymentEventHandlingResult.NoOp(order.Id);
        }

        if (order.Status == OrderStatus.Pending)
        {
            _logger.LogInformation(
                "PaymentCompleted {MessageId} arrived before InventoryReserved was applied for order {OrderId}; applying inferred InventoryReserved transition first.",
                envelope.MessageId,
                order.Id);

            var inferredInventoryTransition = order.ApplyStatusTransition(OrderStatus.InventoryReserved, _clock.UtcNow);
            if (inferredInventoryTransition.IsFailure)
            {
                throw new InvalidOperationException($"Order {order.Id} could not transition Pending -> InventoryReserved before PaymentCompleted: {inferredInventoryTransition.Error.Code}");
            }
        }

        if (order.Status == OrderStatus.InventoryReserved)
        {
            var paymentTransition = order.ApplyStatusTransition(OrderStatus.PaymentCompleted, _clock.UtcNow);
            if (paymentTransition.IsFailure)
            {
                throw new InvalidOperationException($"Order {order.Id} could not transition InventoryReserved -> PaymentCompleted: {paymentTransition.Error.Code}");
            }
        }

        if (order.Status != OrderStatus.PaymentCompleted)
        {
            _logger.LogWarning(
                "Ignoring PaymentCompleted {MessageId} for order {OrderId}. Current status {CurrentStatus} cannot complete payment.",
                envelope.MessageId,
                order.Id,
                order.Status);

            AddProcessedMessage(envelope.MessageId, envelope.EventType);
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return PaymentEventHandlingResult.InvalidTransition(order.Id);
        }

        var completeTransition = order.ApplyStatusTransition(OrderStatus.Completed, _clock.UtcNow);
        if (completeTransition.IsFailure)
        {
            throw new InvalidOperationException($"Order {order.Id} could not transition PaymentCompleted -> Completed: {completeTransition.Error.Code}");
        }

        AddOrderCompletedOutboxMessage(envelope, order);
        AddProcessedMessage(envelope.MessageId, envelope.EventType);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await _orderCache.RemoveAsync(order.Id, cancellationToken);

        _logger.LogInformation("Completed order {OrderId} from PaymentCompleted message {MessageId}.", order.Id, envelope.MessageId);
        return PaymentEventHandlingResult.Updated(order.Id);
    }

    private async Task<PaymentEventHandlingResult> HandleFailedAsync(
        EventEnvelope<PaymentFailed> envelope,
        CancellationToken cancellationToken)
    {
        var alreadyProcessed = await IsProcessedAsync(envelope.MessageId, cancellationToken);
        if (alreadyProcessed)
        {
            return PaymentEventHandlingResult.Duplicate(envelope.Payload.OrderId);
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        var order = await _dbContext.Orders.SingleOrDefaultAsync(order => order.Id == envelope.Payload.OrderId, cancellationToken);
        if (order is null)
        {
            AddProcessedMessage(envelope.MessageId, envelope.EventType);
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            _logger.LogWarning("PaymentFailed {MessageId} references unknown order {OrderId}; marked processed.", envelope.MessageId, envelope.Payload.OrderId);
            return PaymentEventHandlingResult.UnknownOrder(envelope.Payload.OrderId);
        }

        if (order.Status == OrderStatus.PaymentFailed)
        {
            AddProcessedMessage(envelope.MessageId, envelope.EventType);
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return PaymentEventHandlingResult.NoOp(order.Id);
        }

        if (order.Status == OrderStatus.Pending)
        {
            _logger.LogInformation(
                "PaymentFailed {MessageId} arrived before InventoryReserved was applied for order {OrderId}; applying inferred InventoryReserved transition first.",
                envelope.MessageId,
                order.Id);

            var inferredInventoryTransition = order.ApplyStatusTransition(OrderStatus.InventoryReserved, _clock.UtcNow);
            if (inferredInventoryTransition.IsFailure)
            {
                throw new InvalidOperationException($"Order {order.Id} could not transition Pending -> InventoryReserved before PaymentFailed: {inferredInventoryTransition.Error.Code}");
            }
        }

        if (order.Status != OrderStatus.InventoryReserved)
        {
            _logger.LogWarning(
                "Ignoring PaymentFailed {MessageId} for order {OrderId}. Current status {CurrentStatus} cannot fail payment.",
                envelope.MessageId,
                order.Id,
                order.Status);

            AddProcessedMessage(envelope.MessageId, envelope.EventType);
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return PaymentEventHandlingResult.InvalidTransition(order.Id);
        }

        var transition = order.ApplyStatusTransition(OrderStatus.PaymentFailed, _clock.UtcNow);
        if (transition.IsFailure)
        {
            throw new InvalidOperationException($"Order {order.Id} could not transition InventoryReserved -> PaymentFailed: {transition.Error.Code}");
        }

        AddProcessedMessage(envelope.MessageId, envelope.EventType);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await _orderCache.RemoveAsync(order.Id, cancellationToken);

        _logger.LogInformation("Marked order {OrderId} PaymentFailed from message {MessageId}.", order.Id, envelope.MessageId);
        return PaymentEventHandlingResult.Updated(order.Id);
    }

    private Task<bool> IsProcessedAsync(Guid messageId, CancellationToken cancellationToken) =>
        _dbContext.ProcessedMessages.AnyAsync(
            message => message.MessageId == messageId && message.ConsumerName == ConsumerName,
            cancellationToken);

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

    private void AddOrderCompletedOutboxMessage(EventEnvelope<PaymentCompleted> sourceEnvelope, OrderAggregate order)
    {
        var payload = new OrderCompleted(
            order.Id,
            order.CustomerId,
            order.TotalAmount,
            order.Currency);

        var envelope = EventEnvelope.Create(
            payload,
            sourceEnvelope.CorrelationId,
            sourceEnvelope.MessageId,
            "OrderService",
            _clock.UtcNow);

        _dbContext.OutboxMessages.Add(OutboxMessage.Create(
            envelope.MessageId,
            envelope.CorrelationId,
            envelope.EventType,
            envelope.EventVersion,
            EventRoutingKeys.OrderCompleted,
            JsonSerializer.Serialize(envelope, JsonOptions),
            envelope.OccurredOnUtc));
    }
}

public enum PaymentEventHandlingOutcome
{
    Updated = 0,
    Duplicate = 1,
    UnknownOrder = 2,
    InvalidTransition = 3,
    NoOp = 4
}

public sealed record PaymentEventHandlingResult(Guid OrderId, PaymentEventHandlingOutcome Outcome)
{
    public static PaymentEventHandlingResult Updated(Guid orderId) => new(orderId, PaymentEventHandlingOutcome.Updated);
    public static PaymentEventHandlingResult Duplicate(Guid orderId) => new(orderId, PaymentEventHandlingOutcome.Duplicate);
    public static PaymentEventHandlingResult UnknownOrder(Guid orderId) => new(orderId, PaymentEventHandlingOutcome.UnknownOrder);
    public static PaymentEventHandlingResult InvalidTransition(Guid orderId) => new(orderId, PaymentEventHandlingOutcome.InvalidTransition);
    public static PaymentEventHandlingResult NoOp(Guid orderId) => new(orderId, PaymentEventHandlingOutcome.NoOp);
}
