using BuildingBlocks.Contracts;
using BuildingBlocks.Contracts.Events;
using BuildingBlocks.Persistence;
using BuildingBlocks.SharedKernel;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PaymentService.Domain;
using PaymentService.Persistence;

namespace PaymentService.Messaging;

public sealed class PaymentCancellationHandler
{
    public const string ConsumerName = "PaymentService.OrderCancelled";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly PaymentDbContext _dbContext;
    private readonly ISystemClock _clock;
    private readonly ILogger<PaymentCancellationHandler> _logger;

    public PaymentCancellationHandler(
        PaymentDbContext dbContext,
        ISystemClock clock,
        ILogger<PaymentCancellationHandler> logger)
    {
        _dbContext = dbContext;
        _clock = clock;
        _logger = logger;
    }

    public async Task<PaymentCancellationHandlingResult> HandleOrderCancelledAsync(
        EventEnvelope<OrderCancelled> envelope,
        CancellationToken cancellationToken)
    {
        var alreadyProcessed = await _dbContext.ProcessedMessages.AnyAsync(
            message => message.MessageId == envelope.MessageId && message.ConsumerName == ConsumerName,
            cancellationToken);

        if (alreadyProcessed)
        {
            return PaymentCancellationHandlingResult.Duplicate(envelope.Payload.OrderId);
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        await PaymentOrderLock.AcquireAsync(_dbContext, envelope.Payload.OrderId, cancellationToken);

        if (await IsProcessedAsync(envelope.MessageId, cancellationToken))
        {
            await transaction.CommitAsync(cancellationToken);
            return PaymentCancellationHandlingResult.Duplicate(envelope.Payload.OrderId);
        }

        var existingCancellation = await _dbContext.CancelledOrders.SingleOrDefaultAsync(
            cancellation => cancellation.OrderId == envelope.Payload.OrderId,
            cancellationToken);

        var existingPayment = await _dbContext.PaymentAttempts.SingleOrDefaultAsync(
            payment => payment.OrderId == envelope.Payload.OrderId,
            cancellationToken);

        if (existingCancellation is null)
        {
            existingCancellation = PaymentCancelledOrder.Record(
                envelope.Payload.OrderId,
                envelope.Payload.CustomerId,
                envelope.Payload.Reason,
                envelope.OccurredOnUtc);

            _dbContext.CancelledOrders.Add(existingCancellation);
        }

        var wasAlreadyRefundRequired = existingCancellation.Status == PaymentCancellationStatus.RefundRequired;
        if (existingPayment?.Status == PaymentStatus.Completed)
        {
            existingCancellation.MarkRefundRequired(_clock.UtcNow);
            if (!wasAlreadyRefundRequired)
            {
                AddPaymentRefundRequiredOutboxMessage(envelope, existingPayment);
            }

            _logger.LogWarning(
                "Order {OrderId} was cancelled after payment {PaymentId} completed. Cancellation is recorded as refund-required.",
                envelope.Payload.OrderId,
                existingPayment.Id);
        }

        AddProcessedMessage(envelope);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return existingCancellation.Status == PaymentCancellationStatus.RefundRequired
            ? PaymentCancellationHandlingResult.RefundRequired(envelope.Payload.OrderId)
            : PaymentCancellationHandlingResult.Recorded(envelope.Payload.OrderId);
    }

    private Task<bool> IsProcessedAsync(Guid messageId, CancellationToken cancellationToken) =>
        _dbContext.ProcessedMessages.AnyAsync(
            message => message.MessageId == messageId && message.ConsumerName == ConsumerName,
            cancellationToken);

    private void AddPaymentRefundRequiredOutboxMessage(EventEnvelope<OrderCancelled> sourceEnvelope, PaymentAttempt payment)
    {
        var payload = new PaymentRefundRequired(
            payment.OrderId,
            payment.Id,
            payment.ProviderTransactionId ?? $"sim-{payment.OrderId:N}",
            payment.Amount,
            payment.Currency,
            sourceEnvelope.Payload.Reason);

        var envelope = EventEnvelope.Create(
            payload,
            sourceEnvelope.CorrelationId,
            sourceEnvelope.MessageId,
            "PaymentService",
            _clock.UtcNow);

        _dbContext.OutboxMessages.Add(OutboxMessage.Create(
            envelope.MessageId,
            envelope.CorrelationId,
            envelope.EventType,
            envelope.EventVersion,
            EventRoutingKeys.PaymentRefundRequired,
            JsonSerializer.Serialize(envelope, JsonOptions),
            envelope.OccurredOnUtc));
    }

    private void AddProcessedMessage(EventEnvelope<OrderCancelled> envelope)
    {
        _dbContext.ProcessedMessages.Add(new ProcessedMessage
        {
            MessageId = envelope.MessageId,
            EventType = envelope.EventType,
            ConsumerName = ConsumerName,
            ProcessedAtUtc = _clock.UtcNow
        });
    }
}

public enum PaymentCancellationHandlingOutcome
{
    Recorded = 0,
    Duplicate = 1,
    RefundRequired = 2
}

public sealed record PaymentCancellationHandlingResult(Guid OrderId, PaymentCancellationHandlingOutcome Outcome)
{
    public static PaymentCancellationHandlingResult Recorded(Guid orderId) => new(orderId, PaymentCancellationHandlingOutcome.Recorded);
    public static PaymentCancellationHandlingResult Duplicate(Guid orderId) => new(orderId, PaymentCancellationHandlingOutcome.Duplicate);
    public static PaymentCancellationHandlingResult RefundRequired(Guid orderId) => new(orderId, PaymentCancellationHandlingOutcome.RefundRequired);
}
