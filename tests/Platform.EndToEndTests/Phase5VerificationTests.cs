using System.Text.Json;
using BuildingBlocks.Contracts;
using BuildingBlocks.Contracts.Events;
using BuildingBlocks.Persistence;
using BuildingBlocks.SharedKernel;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NotificationService.Messaging;
using NotificationService.Persistence;
using ReportingService.Messaging;
using ReportingService.Persistence;
using Xunit;

namespace PlatformEndToEndTests;

public sealed class Phase5VerificationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Notification_service_should_record_notification_and_notification_sent_outbox_message()
    {
        await using var db = CreateNotificationDbContext();
        var orderId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var envelope = EventEnvelope.Create(
            new OrderCompleted(orderId, customerId, 24.68m, "USD"),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "OrderService",
            DateTime.UtcNow);

        using var document = ToJsonDocument(envelope);
        var handler = new NotificationEventHandler(db, new SystemClock(), NullLogger<NotificationEventHandler>.Instance);
        var result = await handler.HandleAsync(
            envelope.MessageId,
            envelope.CorrelationId,
            envelope.EventType,
            document.RootElement.GetProperty("payload"),
            CancellationToken.None);

        result.Outcome.Should().Be(NotificationHandlingOutcome.Sent);
        db.Notifications.Should().ContainSingle(notification =>
            notification.OrderId == orderId
            && notification.TriggerEventType == nameof(OrderCompleted)
            && notification.Template == "order-completed"
            && notification.Status == "Sent");
        db.ProcessedMessages.Should().ContainSingle(message => message.MessageId == envelope.MessageId);
        db.OutboxMessages.Should().ContainSingle(message =>
            message.EventType == nameof(NotificationSent)
            && message.RoutingKey == EventRoutingKeys.NotificationSent
            && message.Status == OutboxMessageStatus.Pending);
    }

    [Fact]
    public async Task Notification_service_should_skip_duplicate_message_id()
    {
        await using var db = CreateNotificationDbContext();
        var envelope = EventEnvelope.Create(
            new PaymentFailed(Guid.NewGuid(), Guid.NewGuid(), "provider_declined", "Provider declined payment.", 24.68m, "USD"),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "PaymentService",
            DateTime.UtcNow);

        using var document = ToJsonDocument(envelope);
        var handler = new NotificationEventHandler(db, new SystemClock(), NullLogger<NotificationEventHandler>.Instance);
        await handler.HandleAsync(envelope.MessageId, envelope.CorrelationId, envelope.EventType, document.RootElement.GetProperty("payload"), CancellationToken.None);
        var duplicate = await handler.HandleAsync(envelope.MessageId, envelope.CorrelationId, envelope.EventType, document.RootElement.GetProperty("payload"), CancellationToken.None);

        duplicate.Outcome.Should().Be(NotificationHandlingOutcome.Duplicate);
        db.Notifications.Should().ContainSingle();
        db.OutboxMessages.Should().ContainSingle();
        db.ProcessedMessages.Should().ContainSingle(message => message.MessageId == envelope.MessageId);
    }

    [Fact]
    public async Task Reporting_service_should_project_order_lifecycle_into_read_model_and_event_audit()
    {
        await using var db = CreateReportingDbContext();
        var orderId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        var handler = new ReportingProjectionHandler(db, new SystemClock(), NullLogger<ReportingProjectionHandler>.Instance);

        await ProjectAsync(handler, EventEnvelope.Create(
            new OrderCreated(orderId, customerId, "phase5-report", [new OrderCreatedItem(Guid.NewGuid(), 2, 12.34m)], 24.68m, "USD"),
            correlationId,
            correlationId,
            "OrderService",
            DateTime.UtcNow));
        await ProjectAsync(handler, EventEnvelope.Create(
            new InventoryReserved(orderId, 24.68m, "USD", [new InventoryReservedItem(Guid.NewGuid(), 2)]),
            correlationId,
            correlationId,
            "InventoryService",
            DateTime.UtcNow.AddSeconds(1)));
        await ProjectAsync(handler, EventEnvelope.Create(
            new PaymentCompleted(orderId, Guid.NewGuid(), "sim-123", 24.68m, "USD"),
            correlationId,
            correlationId,
            "PaymentService",
            DateTime.UtcNow.AddSeconds(2)));
        await ProjectAsync(handler, EventEnvelope.Create(
            new OrderCompleted(orderId, customerId, 24.68m, "USD"),
            correlationId,
            correlationId,
            "OrderService",
            DateTime.UtcNow.AddSeconds(3)));

        var readModel = await db.Orders.SingleAsync(order => order.OrderId == orderId);
        readModel.CustomerId.Should().Be(customerId);
        readModel.Status.Should().Be("Completed");
        readModel.TotalAmount.Should().Be(24.68m);
        readModel.Currency.Should().Be("USD");
        readModel.LastEventType.Should().Be(nameof(OrderCompleted));
        db.Events.Count(e => e.OrderId == orderId).Should().Be(4);
        db.ProcessedMessages.Count(message => message.ConsumerName == ReportingProjectionHandler.ConsumerName).Should().Be(4);
    }

    [Fact]
    public async Task Reporting_service_should_ignore_duplicate_message_id()
    {
        await using var db = CreateReportingDbContext();
        var envelope = EventEnvelope.Create(
            new OrderCancelled(Guid.NewGuid(), Guid.NewGuid(), "customer_requested"),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "OrderService",
            DateTime.UtcNow);
        var handler = new ReportingProjectionHandler(db, new SystemClock(), NullLogger<ReportingProjectionHandler>.Instance);

        await ProjectAsync(handler, envelope);
        var duplicate = await ProjectAsync(handler, envelope);

        duplicate.Outcome.Should().Be(ReportingProjectionOutcome.Duplicate);
        db.Events.Should().ContainSingle();
        db.ProcessedMessages.Should().ContainSingle(message => message.MessageId == envelope.MessageId);
    }

    private static async Task<ReportingProjectionResult> ProjectAsync<TPayload>(ReportingProjectionHandler handler, EventEnvelope<TPayload> envelope)
    {
        using var document = ToJsonDocument(envelope);
        var root = document.RootElement;
        return await handler.HandleAsync(
            envelope.MessageId,
            envelope.CorrelationId,
            envelope.EventType,
            envelope.Source,
            envelope.OccurredOnUtc,
            root.GetProperty("payload").GetRawText(),
            root.GetProperty("payload"),
            CancellationToken.None);
    }

    private static JsonDocument ToJsonDocument<TPayload>(EventEnvelope<TPayload> envelope) =>
        JsonDocument.Parse(JsonSerializer.Serialize(envelope, JsonOptions));


    [Fact]
    public async Task Notification_service_should_send_notification_for_payment_refund_required()
    {
        await using var db = CreateNotificationDbContext();
        var handler = new NotificationEventHandler(db, new SystemClock(), NullLogger<NotificationEventHandler>.Instance);
        var envelope = EventEnvelope.Create(
            new PaymentRefundRequired(Guid.NewGuid(), Guid.NewGuid(), "sim-refund", 24.68m, "USD", "customer_requested"),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "PaymentService",
            DateTime.UtcNow);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(envelope, JsonOptions));

        var result = await handler.HandleAsync(envelope.MessageId, envelope.CorrelationId, envelope.EventType, document.RootElement.GetProperty("payload"), CancellationToken.None);

        result.Outcome.Should().Be(NotificationHandlingOutcome.Sent);
        db.Notifications.Should().ContainSingle(notification =>
            notification.TriggerEventType == nameof(PaymentRefundRequired) &&
            notification.Template == "payment-refund-required");
    }

    [Fact]
    public async Task Reporting_service_should_project_payment_refund_required_status()
    {
        await using var db = CreateReportingDbContext();
        var handler = new ReportingProjectionHandler(db, new SystemClock(), NullLogger<ReportingProjectionHandler>.Instance);
        var orderId = Guid.NewGuid();
        var envelope = EventEnvelope.Create(
            new PaymentRefundRequired(orderId, Guid.NewGuid(), "sim-refund", 24.68m, "USD", "customer_requested"),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "PaymentService",
            DateTime.UtcNow);
        var json = JsonSerializer.Serialize(envelope, JsonOptions);
        using var document = JsonDocument.Parse(json);

        var result = await handler.HandleAsync(
            envelope.MessageId,
            envelope.CorrelationId,
            envelope.EventType,
            envelope.Source,
            envelope.OccurredOnUtc,
            json,
            document.RootElement.GetProperty("payload"),
            CancellationToken.None);

        result.Outcome.Should().Be(ReportingProjectionOutcome.Projected);
        db.Orders.Should().ContainSingle(order =>
            order.OrderId == orderId &&
            order.Status == "RefundRequired" &&
            order.LastEventType == nameof(PaymentRefundRequired));
        db.Events.Should().ContainSingle(evt => evt.EventType == nameof(PaymentRefundRequired));
    }


    [Fact]
    public async Task Notification_service_should_send_notification_for_payment_duplicate_rejected()
    {
        await using var db = CreateNotificationDbContext();
        var handler = new NotificationEventHandler(db, new SystemClock(), NullLogger<NotificationEventHandler>.Instance);
        var envelope = EventEnvelope.Create(
            new PaymentDuplicateRejected(Guid.NewGuid(), Guid.NewGuid(), "sim-existing", "Completed", 24.68m, "USD", "order_already_paid"),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "PaymentService",
            DateTime.UtcNow);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(envelope, JsonOptions));

        var result = await handler.HandleAsync(envelope.MessageId, envelope.CorrelationId, envelope.EventType, document.RootElement.GetProperty("payload"), CancellationToken.None);

        result.Outcome.Should().Be(NotificationHandlingOutcome.Sent);
        db.Notifications.Should().ContainSingle(notification =>
            notification.TriggerEventType == nameof(PaymentDuplicateRejected) &&
            notification.Template == "payment-duplicate-rejected");
    }

    [Fact]
    public async Task Reporting_service_should_audit_payment_duplicate_rejected_without_changing_terminal_order_status()
    {
        await using var db = CreateReportingDbContext();
        var handler = new ReportingProjectionHandler(db, new SystemClock(), NullLogger<ReportingProjectionHandler>.Instance);
        var orderId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();

        await ProjectAsync(handler, EventEnvelope.Create(
            new OrderCreated(orderId, Guid.NewGuid(), "phase7-duplicate", [], 24.68m, "USD"),
            correlationId,
            correlationId,
            "OrderService",
            DateTime.UtcNow));
        await ProjectAsync(handler, EventEnvelope.Create(
            new OrderCompleted(orderId, Guid.NewGuid(), 24.68m, "USD"),
            correlationId,
            correlationId,
            "OrderService",
            DateTime.UtcNow.AddSeconds(1)));
        await ProjectAsync(handler, EventEnvelope.Create(
            new PaymentDuplicateRejected(orderId, Guid.NewGuid(), "sim-existing", "Completed", 24.68m, "USD", "order_already_paid"),
            correlationId,
            correlationId,
            "PaymentService",
            DateTime.UtcNow.AddSeconds(2)));

        var order = await db.Orders.SingleAsync(item => item.OrderId == orderId);
        order.Status.Should().Be("Completed");
        order.LastEventType.Should().Be(nameof(PaymentDuplicateRejected));
        db.Events.Should().ContainSingle(evt => evt.EventType == nameof(PaymentDuplicateRejected));
    }

    private static NotificationDbContext CreateNotificationDbContext()
    {
        var options = new DbContextOptionsBuilder<NotificationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        return new NotificationDbContext(options);
    }

    private static ReportingDbContext CreateReportingDbContext()
    {
        var options = new DbContextOptionsBuilder<ReportingDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        return new ReportingDbContext(options);
    }
}
