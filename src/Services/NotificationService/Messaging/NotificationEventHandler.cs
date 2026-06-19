using System.Text.Json;
using BuildingBlocks.Contracts;
using BuildingBlocks.Contracts.Events;
using BuildingBlocks.Persistence;
using BuildingBlocks.SharedKernel;
using Microsoft.EntityFrameworkCore;
using NotificationService.Domain;
using NotificationService.Persistence;

namespace NotificationService.Messaging;

public sealed class NotificationEventHandler
{
    public const string ConsumerName = "NotificationService.Events";
    private const string ServiceName = "NotificationService";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly NotificationDbContext _dbContext;
    private readonly ISystemClock _clock;
    private readonly ILogger<NotificationEventHandler> _logger;

    public NotificationEventHandler(NotificationDbContext dbContext, ISystemClock clock, ILogger<NotificationEventHandler> logger)
    {
        _dbContext = dbContext;
        _clock = clock;
        _logger = logger;
    }

    public async Task<NotificationHandlingResult> HandleAsync(
        Guid messageId,
        Guid correlationId,
        string eventType,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var alreadyProcessed = await _dbContext.ProcessedMessages.AnyAsync(
            message => message.MessageId == messageId && message.ConsumerName == ConsumerName,
            cancellationToken);

        if (alreadyProcessed)
        {
            return NotificationHandlingResult.Duplicate(messageId);
        }

        var decision = TryCreateNotificationDecision(eventType, payload);

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        if (decision is null)
        {
            AddProcessedMessage(messageId, eventType);
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return NotificationHandlingResult.Ignored(messageId);
        }

        var notification = NotificationMessage.Sent(
            messageId,
            decision.OrderId,
            eventType,
            decision.Channel,
            decision.Recipient,
            decision.Template,
            _clock.UtcNow);

        _dbContext.Notifications.Add(notification);

        var sentEvent = new NotificationSent(decision.OrderId, decision.Channel, decision.Recipient, decision.Template);
        var envelope = EventEnvelope.Create(sentEvent, correlationId, messageId, ServiceName, _clock.UtcNow);
        _dbContext.OutboxMessages.Add(OutboxMessage.Create(
            envelope.MessageId,
            envelope.CorrelationId,
            envelope.EventType,
            envelope.EventVersion,
            EventRoutingKeys.NotificationSent,
            JsonSerializer.Serialize(envelope, JsonOptions),
            envelope.OccurredOnUtc));

        AddProcessedMessage(messageId, eventType);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        _logger.LogInformation(
            "Recorded notification {Template} for order {OrderId} from event {EventType}.",
            decision.Template,
            decision.OrderId,
            eventType);

        return NotificationHandlingResult.Sent(messageId, decision.OrderId);
    }

    private static NotificationDecision? TryCreateNotificationDecision(string eventType, JsonElement payload)
    {
        return eventType switch
        {
            nameof(OrderCreated) => new NotificationDecision(
                ReadGuid(payload, "orderId"),
                "Email",
                $"customer-{ReadGuid(payload, "customerId"):D}@example.local",
                "order-created"),
            nameof(OrderCompleted) => new NotificationDecision(
                ReadGuid(payload, "orderId"),
                "Email",
                $"order-{ReadGuid(payload, "orderId"):D}@example.local",
                "order-completed"),
            nameof(OrderCancelled) => new NotificationDecision(
                ReadGuid(payload, "orderId"),
                "Email",
                $"customer-{ReadGuid(payload, "customerId"):D}@example.local",
                "order-cancelled"),
            nameof(PaymentCompleted) => new NotificationDecision(
                ReadGuid(payload, "orderId"),
                "Email",
                $"order-{ReadGuid(payload, "orderId"):D}@example.local",
                "payment-completed"),
            nameof(PaymentFailed) => new NotificationDecision(
                ReadGuid(payload, "orderId"),
                "Email",
                $"order-{ReadGuid(payload, "orderId"):D}@example.local",
                "payment-failed"),
            nameof(PaymentRefundRequired) => new NotificationDecision(
                ReadGuid(payload, "orderId"),
                "Email",
                $"order-{ReadGuid(payload, "orderId"):D}@example.local",
                "payment-refund-required"),
            nameof(PaymentDuplicateRejected) => new NotificationDecision(
                ReadGuid(payload, "orderId"),
                "Email",
                $"order-{ReadGuid(payload, "orderId"):D}@example.local",
                "payment-duplicate-rejected"),
            _ => null
        };
    }

    private static Guid ReadGuid(JsonElement payload, string propertyName)
    {
        if (!payload.TryGetProperty(propertyName, out var property) || !property.TryGetGuid(out var value))
        {
            throw new JsonException($"Notification payload does not contain a valid {propertyName}.");
        }

        return value;
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

    private sealed record NotificationDecision(Guid OrderId, string Channel, string Recipient, string Template);
}

public enum NotificationHandlingOutcome
{
    Sent = 0,
    Duplicate = 1,
    Ignored = 2
}

public sealed record NotificationHandlingResult(Guid MessageId, NotificationHandlingOutcome Outcome, Guid? OrderId = null)
{
    public static NotificationHandlingResult Sent(Guid messageId, Guid orderId) => new(messageId, NotificationHandlingOutcome.Sent, orderId);
    public static NotificationHandlingResult Duplicate(Guid messageId) => new(messageId, NotificationHandlingOutcome.Duplicate);
    public static NotificationHandlingResult Ignored(Guid messageId) => new(messageId, NotificationHandlingOutcome.Ignored);
}
