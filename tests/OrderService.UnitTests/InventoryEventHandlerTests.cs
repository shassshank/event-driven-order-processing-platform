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

public sealed class InventoryEventHandlerTests
{
    [Fact]
    public async Task Should_mark_order_inventory_reserved_when_inventory_reserved_event_arrives()
    {
        await using var db = CreateDbContext();
        var order = CreateOrder();
        db.Orders.Add(order);
        await db.SaveChangesAsync();

        var handler = CreateHandler(db);
        var envelope = EventEnvelope.Create(
            new InventoryReserved(order.Id, order.TotalAmount, order.Currency, [new InventoryReservedItem(order.Items.Single().ProductId, order.Items.Single().Quantity)]),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "InventoryService",
            DateTime.UtcNow);

        var result = await handler.HandleInventoryReservedAsync(envelope, CancellationToken.None);

        result.Outcome.Should().Be(InventoryEventHandlingOutcome.Updated);
        var updated = await db.Orders.SingleAsync(saved => saved.Id == order.Id);
        updated.Status.Should().Be(OrderStatus.InventoryReserved);
        db.ProcessedMessages.Should().ContainSingle(message => message.MessageId == envelope.MessageId);
    }

    [Fact]
    public async Task Should_mark_order_inventory_failed_when_inventory_failure_event_arrives()
    {
        await using var db = CreateDbContext();
        var order = CreateOrder();
        db.Orders.Add(order);
        await db.SaveChangesAsync();

        var handler = CreateHandler(db);
        var envelope = EventEnvelope.Create(
            new InventoryReservationFailed(order.Id, "Insufficient stock", [new InventoryFailureItem(order.Items.Single().ProductId, 2, 0, "Insufficient stock.")]),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "InventoryService",
            DateTime.UtcNow);

        var result = await handler.HandleInventoryReservationFailedAsync(envelope, CancellationToken.None);

        result.Outcome.Should().Be(InventoryEventHandlingOutcome.Updated);
        var updated = await db.Orders.SingleAsync(saved => saved.Id == order.Id);
        updated.Status.Should().Be(OrderStatus.InventoryFailed);
        db.ProcessedMessages.Should().ContainSingle(message => message.MessageId == envelope.MessageId);
    }

    [Fact]
    public async Task Should_skip_duplicate_inventory_event_message()
    {
        await using var db = CreateDbContext();
        var order = CreateOrder();
        db.Orders.Add(order);
        await db.SaveChangesAsync();

        var handler = CreateHandler(db);
        var envelope = EventEnvelope.Create(
            new InventoryReserved(order.Id, order.TotalAmount, order.Currency, [new InventoryReservedItem(order.Items.Single().ProductId, order.Items.Single().Quantity)]),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "InventoryService",
            DateTime.UtcNow);

        await handler.HandleInventoryReservedAsync(envelope, CancellationToken.None);
        var duplicate = await handler.HandleInventoryReservedAsync(envelope, CancellationToken.None);

        duplicate.Outcome.Should().Be(InventoryEventHandlingOutcome.Duplicate);
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

    private static InventoryEventHandler CreateHandler(OrderDbContext dbContext) =>
        new(dbContext, new SystemClock(), new NoOpOrderCache(), NullLogger<InventoryEventHandler>.Instance);

    private static OrderAggregate CreateOrder() =>
        OrderAggregate.Create(
            Guid.NewGuid(),
            Guid.NewGuid().ToString("N"),
            [(Guid.NewGuid(), 2, 12.34m)],
            "USD",
            DateTime.UtcNow);
}
