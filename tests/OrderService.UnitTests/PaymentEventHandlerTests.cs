using BuildingBlocks.Contracts;
using BuildingBlocks.Contracts.Events;
using BuildingBlocks.SharedKernel;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using OrderService.Domain;
using OrderService.Messaging;
using OrderService.Caching;
using OrderService.Persistence;
using Xunit;

namespace OrderService.UnitTests;

public sealed class PaymentEventHandlerTests
{
    [Fact]
    public async Task Should_complete_order_when_payment_completed_event_arrives()
    {
        await using var db = CreateDbContext();
        var order = CreateOrder();
        order.ApplyStatusTransition(OrderStatus.InventoryReserved, DateTime.UtcNow);
        db.Orders.Add(order);
        await db.SaveChangesAsync();

        var envelope = EventEnvelope.Create(
            new PaymentCompleted(order.Id, Guid.NewGuid(), "txn-1", order.TotalAmount, order.Currency),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "PaymentService",
            DateTime.UtcNow);

        var result = await CreateHandler(db).HandlePaymentCompletedAsync(envelope, CancellationToken.None);

        result.Outcome.Should().Be(PaymentEventHandlingOutcome.Updated);
        var updated = await db.Orders.SingleAsync(saved => saved.Id == order.Id);
        updated.Status.Should().Be(OrderStatus.Completed);
        db.ProcessedMessages.Should().ContainSingle(message => message.MessageId == envelope.MessageId);
        db.OutboxMessages.Should().ContainSingle(message => message.EventType == nameof(OrderCompleted));
    }

    [Fact]
    public async Task Should_mark_order_payment_failed_when_payment_failed_event_arrives()
    {
        await using var db = CreateDbContext();
        var order = CreateOrder();
        order.ApplyStatusTransition(OrderStatus.InventoryReserved, DateTime.UtcNow);
        db.Orders.Add(order);
        await db.SaveChangesAsync();

        var envelope = EventEnvelope.Create(
            new PaymentFailed(order.Id, Guid.NewGuid(), "provider_declined", "Declined", order.TotalAmount, order.Currency),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "PaymentService",
            DateTime.UtcNow);

        var result = await CreateHandler(db).HandlePaymentFailedAsync(envelope, CancellationToken.None);

        result.Outcome.Should().Be(PaymentEventHandlingOutcome.Updated);
        var updated = await db.Orders.SingleAsync(saved => saved.Id == order.Id);
        updated.Status.Should().Be(OrderStatus.PaymentFailed);
        db.ProcessedMessages.Should().ContainSingle(message => message.MessageId == envelope.MessageId);
    }


    [Fact]
    public async Task Should_complete_order_when_payment_completed_arrives_before_inventory_reserved_is_applied()
    {
        await using var db = CreateDbContext();
        var order = CreateOrder();
        db.Orders.Add(order);
        await db.SaveChangesAsync();

        var envelope = EventEnvelope.Create(
            new PaymentCompleted(order.Id, Guid.NewGuid(), "txn-out-of-order", order.TotalAmount, order.Currency),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "PaymentService",
            DateTime.UtcNow);

        var result = await CreateHandler(db).HandlePaymentCompletedAsync(envelope, CancellationToken.None);

        result.Outcome.Should().Be(PaymentEventHandlingOutcome.Updated);
        var updated = await db.Orders.SingleAsync(saved => saved.Id == order.Id);
        updated.Status.Should().Be(OrderStatus.Completed);
        db.ProcessedMessages.Should().ContainSingle(message => message.MessageId == envelope.MessageId);
        db.OutboxMessages.Should().ContainSingle(message => message.EventType == nameof(OrderCompleted));
    }

    [Fact]
    public async Task Should_mark_order_payment_failed_when_payment_failed_arrives_before_inventory_reserved_is_applied()
    {
        await using var db = CreateDbContext();
        var order = CreateOrder();
        db.Orders.Add(order);
        await db.SaveChangesAsync();

        var envelope = EventEnvelope.Create(
            new PaymentFailed(order.Id, Guid.NewGuid(), "provider_declined", "Declined", order.TotalAmount, order.Currency),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "PaymentService",
            DateTime.UtcNow);

        var result = await CreateHandler(db).HandlePaymentFailedAsync(envelope, CancellationToken.None);

        result.Outcome.Should().Be(PaymentEventHandlingOutcome.Updated);
        var updated = await db.Orders.SingleAsync(saved => saved.Id == order.Id);
        updated.Status.Should().Be(OrderStatus.PaymentFailed);
        db.ProcessedMessages.Should().ContainSingle(message => message.MessageId == envelope.MessageId);
    }

    private static OrderDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<OrderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        return new OrderDbContext(options);
    }

    private static PaymentEventHandler CreateHandler(OrderDbContext dbContext) =>
        new(dbContext, new SystemClock(), new NoOpOrderCache(), NullLogger<PaymentEventHandler>.Instance);

    private static OrderAggregate CreateOrder() =>
        OrderAggregate.Create(
            Guid.NewGuid(),
            Guid.NewGuid().ToString("N"),
            [(Guid.NewGuid(), 2, 12.34m)],
            "USD",
            DateTime.UtcNow);
}
