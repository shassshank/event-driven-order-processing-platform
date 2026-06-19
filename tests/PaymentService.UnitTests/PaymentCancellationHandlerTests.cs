using BuildingBlocks.Contracts;
using BuildingBlocks.Contracts.Events;
using BuildingBlocks.SharedKernel;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using PaymentService.Domain;
using PaymentService.Messaging;
using PaymentService.Persistence;
using Xunit;

namespace PaymentServiceUnitTests;

public sealed class PaymentCancellationHandlerTests
{
    [Fact]
    public async Task Should_record_order_cancelled_message_for_payment_service()
    {
        await using var db = CreateDbContext();
        var handler = CreateHandler(db);
        var envelope = CreateOrderCancelledEnvelope();

        var result = await handler.HandleOrderCancelledAsync(envelope, CancellationToken.None);

        result.Outcome.Should().Be(PaymentCancellationHandlingOutcome.Recorded);
        db.CancelledOrders.Should().ContainSingle(cancellation =>
            cancellation.OrderId == envelope.Payload.OrderId &&
            cancellation.Status == PaymentCancellationStatus.Recorded);
        db.ProcessedMessages.Should().ContainSingle(message =>
            message.MessageId == envelope.MessageId &&
            message.ConsumerName == PaymentCancellationHandler.ConsumerName);
    }

    [Fact]
    public async Task Should_not_record_duplicate_cancellation_for_same_order_with_different_message_id()
    {
        await using var db = CreateDbContext();
        var handler = CreateHandler(db);
        var orderId = Guid.NewGuid();

        await handler.HandleOrderCancelledAsync(CreateOrderCancelledEnvelope(orderId), CancellationToken.None);
        var duplicate = await handler.HandleOrderCancelledAsync(CreateOrderCancelledEnvelope(orderId), CancellationToken.None);

        duplicate.Outcome.Should().Be(PaymentCancellationHandlingOutcome.Recorded);
        db.CancelledOrders.Should().ContainSingle(cancellation => cancellation.OrderId == orderId);
        db.ProcessedMessages.Count().Should().Be(2);
    }

    [Fact]
    public async Task Should_mark_refund_required_when_order_cancelled_after_payment_completed()
    {
        await using var db = CreateDbContext();
        var orderId = Guid.NewGuid();
        var payment = PaymentAttempt.Create(orderId, 24.68m, "USD", DateTime.UtcNow);
        payment.Complete($"sim-{orderId:N}", DateTime.UtcNow);
        db.PaymentAttempts.Add(payment);
        await db.SaveChangesAsync();
        var handler = CreateHandler(db);

        var result = await handler.HandleOrderCancelledAsync(CreateOrderCancelledEnvelope(orderId), CancellationToken.None);

        result.Outcome.Should().Be(PaymentCancellationHandlingOutcome.RefundRequired);
        db.CancelledOrders.Should().ContainSingle(cancellation =>
            cancellation.OrderId == orderId &&
            cancellation.Status == PaymentCancellationStatus.RefundRequired);
        db.OutboxMessages.Should().ContainSingle(message =>
            message.EventType == nameof(PaymentRefundRequired) &&
            message.RoutingKey == EventRoutingKeys.PaymentRefundRequired);
    }

    [Fact]
    public async Task Should_not_publish_duplicate_refund_required_event_for_repeated_order_cancelled_messages()
    {
        await using var db = CreateDbContext();
        var orderId = Guid.NewGuid();
        var payment = PaymentAttempt.Create(orderId, 24.68m, "USD", DateTime.UtcNow);
        payment.Complete($"sim-{orderId:N}", DateTime.UtcNow);
        db.PaymentAttempts.Add(payment);
        await db.SaveChangesAsync();
        var handler = CreateHandler(db);

        await handler.HandleOrderCancelledAsync(CreateOrderCancelledEnvelope(orderId), CancellationToken.None);
        var second = await handler.HandleOrderCancelledAsync(CreateOrderCancelledEnvelope(orderId), CancellationToken.None);

        second.Outcome.Should().Be(PaymentCancellationHandlingOutcome.RefundRequired);
        db.CancelledOrders.Should().ContainSingle(cancellation =>
            cancellation.OrderId == orderId &&
            cancellation.Status == PaymentCancellationStatus.RefundRequired);
        db.OutboxMessages.Should().ContainSingle(message => message.EventType == nameof(PaymentRefundRequired));
        db.ProcessedMessages.Count().Should().Be(2);
    }

    [Fact]
    public async Task Should_skip_same_order_cancelled_message_when_already_processed()
    {
        await using var db = CreateDbContext();
        var handler = CreateHandler(db);
        var envelope = CreateOrderCancelledEnvelope();

        await handler.HandleOrderCancelledAsync(envelope, CancellationToken.None);
        var duplicate = await handler.HandleOrderCancelledAsync(envelope, CancellationToken.None);

        duplicate.Outcome.Should().Be(PaymentCancellationHandlingOutcome.Duplicate);
        db.CancelledOrders.Should().ContainSingle(cancellation => cancellation.OrderId == envelope.Payload.OrderId);
        db.ProcessedMessages.Should().ContainSingle(message =>
            message.MessageId == envelope.MessageId &&
            message.ConsumerName == PaymentCancellationHandler.ConsumerName);
    }

    private static PaymentDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<PaymentDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        return new PaymentDbContext(options);
    }

    private static PaymentCancellationHandler CreateHandler(PaymentDbContext dbContext) =>
        new(dbContext, new SystemClock(), NullLogger<PaymentCancellationHandler>.Instance);

    private static EventEnvelope<OrderCancelled> CreateOrderCancelledEnvelope(Guid? orderId = null)
    {
        var payload = new OrderCancelled(orderId ?? Guid.NewGuid(), Guid.NewGuid(), "customer_requested");
        return EventEnvelope.Create(payload, Guid.NewGuid(), Guid.NewGuid(), "OrderService", DateTime.UtcNow);
    }
}
