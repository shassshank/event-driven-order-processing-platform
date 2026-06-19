using BuildingBlocks.Contracts;
using BuildingBlocks.Contracts.Events;
using BuildingBlocks.Persistence;
using BuildingBlocks.SharedKernel;
using FluentAssertions;
using InventoryService.Domain;
using InventoryService.Messaging;
using InventoryService.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InventoryService.UnitTests;

public sealed class InventoryReservationServiceTests
{
    [Fact]
    public async Task Should_reserve_inventory_when_order_created_has_available_stock()
    {
        await using var db = CreateDbContext();
        var productId = Guid.NewGuid();
        db.Products.Add(Product.Create(productId, "SKU-1", "Product 1", 5, DateTime.UtcNow));
        await db.SaveChangesAsync();

        var envelope = CreateOrderCreatedEnvelope(productId, quantity: 2);
        var service = CreateService(db);

        var result = await service.HandleOrderCreatedAsync(envelope, CancellationToken.None);

        result.Outcome.Should().Be(InventoryReservationOutcome.Reserved);
        var product = await db.Products.SingleAsync(product => product.Id == productId);
        product.AvailableStock.Should().Be(3);
        product.ReservedStock.Should().Be(2);
        db.Reservations.Should().ContainSingle(reservation => reservation.OrderId == envelope.Payload.OrderId);
        db.ProcessedMessages.Should().ContainSingle(message => message.MessageId == envelope.MessageId);
        db.OutboxMessages.Should().ContainSingle(message => message.EventType == nameof(InventoryReserved)
            && message.RoutingKey == EventRoutingKeys.InventoryReserved
            && message.CorrelationId == envelope.CorrelationId);
    }

    [Fact]
    public async Task Should_not_reserve_twice_for_duplicate_order_created_message()
    {
        await using var db = CreateDbContext();
        var productId = Guid.NewGuid();
        db.Products.Add(Product.Create(productId, "SKU-1", "Product 1", 5, DateTime.UtcNow));
        await db.SaveChangesAsync();

        var envelope = CreateOrderCreatedEnvelope(productId, quantity: 2);
        var service = CreateService(db);

        await service.HandleOrderCreatedAsync(envelope, CancellationToken.None);
        var duplicateResult = await service.HandleOrderCreatedAsync(envelope, CancellationToken.None);

        duplicateResult.Outcome.Should().Be(InventoryReservationOutcome.Duplicate);
        var product = await db.Products.SingleAsync(product => product.Id == productId);
        product.AvailableStock.Should().Be(3);
        product.ReservedStock.Should().Be(2);
        db.ProcessedMessages.Should().ContainSingle(message => message.MessageId == envelope.MessageId);
        db.OutboxMessages.Should().ContainSingle(message => message.EventType == nameof(InventoryReserved));
    }

    [Fact]
    public async Task Should_not_reserve_twice_for_same_order_with_different_message_id()
    {
        await using var db = CreateDbContext();
        var productId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        db.Products.Add(Product.Create(productId, "SKU-1", "Product 1", 5, DateTime.UtcNow));
        await db.SaveChangesAsync();

        var firstEnvelope = CreateOrderCreatedEnvelope(productId, quantity: 2, orderId);
        var duplicateLogicalEnvelope = CreateOrderCreatedEnvelope(productId, quantity: 2, orderId);
        var service = CreateService(db);

        await service.HandleOrderCreatedAsync(firstEnvelope, CancellationToken.None);
        var duplicateResult = await service.HandleOrderCreatedAsync(duplicateLogicalEnvelope, CancellationToken.None);

        duplicateResult.Outcome.Should().Be(InventoryReservationOutcome.Duplicate);
        var product = await db.Products.SingleAsync(product => product.Id == productId);
        product.AvailableStock.Should().Be(3);
        product.ReservedStock.Should().Be(2);
        db.Reservations.Should().ContainSingle(reservation => reservation.OrderId == orderId);
        db.ProcessedMessages.Should().Contain(message => message.MessageId == firstEnvelope.MessageId);
        db.ProcessedMessages.Should().Contain(message => message.MessageId == duplicateLogicalEnvelope.MessageId);
        db.OutboxMessages.Should().ContainSingle(message => message.EventType == nameof(InventoryReserved));
    }



    [Fact]
    public async Task Should_not_reserve_when_order_was_cancelled_before_order_created_arrives()
    {
        await using var db = CreateDbContext();
        var productId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        db.Products.Add(Product.Create(productId, "SKU-1", "Product 1", 5, DateTime.UtcNow));
        db.CancelledOrders.Add(CancelledOrder.Create(orderId, Guid.NewGuid(), "customer_requested", Guid.NewGuid(), DateTime.UtcNow));
        await db.SaveChangesAsync();

        var envelope = CreateOrderCreatedEnvelope(productId, quantity: 2, orderId);
        var service = CreateService(db);

        var result = await service.HandleOrderCreatedAsync(envelope, CancellationToken.None);

        result.Outcome.Should().Be(InventoryReservationOutcome.Cancelled);
        var product = await db.Products.SingleAsync(product => product.Id == productId);
        product.AvailableStock.Should().Be(5);
        product.ReservedStock.Should().Be(0);
        db.Reservations.Should().BeEmpty();
        db.OutboxMessages.Should().BeEmpty();
        db.ProcessedMessages.Should().ContainSingle(message => message.MessageId == envelope.MessageId
            && message.ConsumerName == InventoryReservationService.ConsumerName);
    }

    [Fact]
    public async Task Should_fail_when_product_does_not_exist()
    {
        await using var db = CreateDbContext();
        var envelope = CreateOrderCreatedEnvelope(Guid.NewGuid(), quantity: 2);
        var service = CreateService(db);

        var result = await service.HandleOrderCreatedAsync(envelope, CancellationToken.None);

        result.Outcome.Should().Be(InventoryReservationOutcome.Failed);
        result.FailedItems.Should().ContainSingle(item => item.Reason == "Product does not exist.");
        db.ProcessedMessages.Should().ContainSingle(message => message.MessageId == envelope.MessageId);
        db.OutboxMessages.Should().ContainSingle(message => message.EventType == nameof(InventoryReservationFailed)
            && message.RoutingKey == EventRoutingKeys.InventoryReservationFailed
            && message.CorrelationId == envelope.CorrelationId);
    }

    private static InventoryDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        return new InventoryDbContext(options);
    }

    private static InventoryReservationService CreateService(InventoryDbContext dbContext) =>
        new(dbContext, new SystemClock(), NullLogger<InventoryReservationService>.Instance);

    private static EventEnvelope<OrderCreated> CreateOrderCreatedEnvelope(Guid productId, int quantity, Guid? orderId = null)
    {
        var payload = new OrderCreated(
            orderId ?? Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid().ToString("N"),
            [new OrderCreatedItem(productId, quantity, 12.34m)],
            quantity * 12.34m,
            "USD");

        return EventEnvelope.Create(payload, Guid.NewGuid(), null, "OrderService", DateTime.UtcNow);
    }
}
