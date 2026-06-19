using BuildingBlocks.Contracts;
using BuildingBlocks.Contracts.Events;
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

public sealed class InventoryReleaseServiceTests
{
    [Fact]
    public async Task Should_release_reserved_inventory_when_order_cancelled()
    {
        await using var db = CreateDbContext();
        var productId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var product = Product.Create(productId, "SKU-CANCEL", "Cancelled Product", 5, now);
        product.Reserve(2, now);
        db.Products.Add(product);
        db.Reservations.Add(InventoryReservation.Reserved(orderId, productId, 2, now));
        await db.SaveChangesAsync();

        var envelope = CreateOrderCancelledEnvelope(orderId);
        var service = CreateService(db);

        var result = await service.HandleOrderCancelledAsync(envelope, CancellationToken.None);

        result.Outcome.Should().Be(InventoryReleaseOutcome.Released);
        var updatedProduct = await db.Products.SingleAsync(stored => stored.Id == productId);
        updatedProduct.AvailableStock.Should().Be(5);
        updatedProduct.ReservedStock.Should().Be(0);
        db.Reservations.Should().ContainSingle(reservation => reservation.OrderId == orderId
            && reservation.ProductId == productId
            && reservation.Status == InventoryReservationStatus.Released);
        db.ProcessedMessages.Should().ContainSingle(message => message.MessageId == envelope.MessageId
            && message.ConsumerName == InventoryReleaseService.OrderCancelledConsumerName
            && message.EventType == nameof(OrderCancelled));
        db.OutboxMessages.Should().ContainSingle(message => message.EventType == nameof(InventoryReleased)
            && message.RoutingKey == EventRoutingKeys.InventoryReleased
            && message.CorrelationId == envelope.CorrelationId);
    }

    [Fact]
    public async Task Should_not_release_twice_for_duplicate_order_cancelled_message()
    {
        await using var db = CreateDbContext();
        var productId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var product = Product.Create(productId, "SKU-CANCEL", "Cancelled Product", 5, now);
        product.Reserve(2, now);
        db.Products.Add(product);
        db.Reservations.Add(InventoryReservation.Reserved(orderId, productId, 2, now));
        await db.SaveChangesAsync();

        var envelope = CreateOrderCancelledEnvelope(orderId);
        var service = CreateService(db);

        await service.HandleOrderCancelledAsync(envelope, CancellationToken.None);
        var duplicateResult = await service.HandleOrderCancelledAsync(envelope, CancellationToken.None);

        duplicateResult.Outcome.Should().Be(InventoryReleaseOutcome.Duplicate);
        var updatedProduct = await db.Products.SingleAsync(stored => stored.Id == productId);
        updatedProduct.AvailableStock.Should().Be(5);
        updatedProduct.ReservedStock.Should().Be(0);
        db.OutboxMessages.Should().ContainSingle(message => message.EventType == nameof(InventoryReleased));
        db.ProcessedMessages.Should().ContainSingle(message => message.MessageId == envelope.MessageId
            && message.ConsumerName == InventoryReleaseService.OrderCancelledConsumerName);
    }

    private static InventoryDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        return new InventoryDbContext(options);
    }

    private static InventoryReleaseService CreateService(InventoryDbContext dbContext) =>
        new(dbContext, new SystemClock(), NullLogger<InventoryReleaseService>.Instance);

    private static EventEnvelope<OrderCancelled> CreateOrderCancelledEnvelope(Guid orderId)
    {
        var payload = new OrderCancelled(orderId, Guid.NewGuid(), "customer_requested");
        return EventEnvelope.Create(payload, Guid.NewGuid(), null, "OrderService", DateTime.UtcNow);
    }
}
