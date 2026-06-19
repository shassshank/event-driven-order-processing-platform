using System.Text.Json;
using BuildingBlocks.Contracts;
using BuildingBlocks.Contracts.Events;
using BuildingBlocks.Observability;
using BuildingBlocks.Persistence;
using BuildingBlocks.SharedKernel;
using FluentAssertions;
using InventoryService.Domain;
using InventoryService.Messaging;
using InventoryService.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderService.Caching;
using OrderService.Domain;
using OrderService.Features.Orders;
using OrderService.Messaging;
using OrderService.Persistence;
using PaymentService.Domain;
using PaymentService.Messaging;
using PaymentService.Persistence;
using Xunit;

namespace PlatformEndToEndTests;

public sealed class Phase4VerificationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Always_success_workflow_should_complete_order()
    {
        await using var orderDb = CreateOrderDbContext();
        await using var inventoryDb = CreateInventoryDbContext();
        await using var paymentDb = CreatePaymentDbContext();
        var productId = Guid.NewGuid();
        inventoryDb.Products.Add(Product.Create(productId, "SKU-PHASE4", "Phase 4 Product", 100, DateTime.UtcNow));
        await inventoryDb.SaveChangesAsync();

        var orderCreated = await CreateOrderAndReadCreatedEvent(orderDb, productId, "phase4-auto-happy");

        var inventoryReservation = await CreateInventoryReservationService(inventoryDb)
            .HandleOrderCreatedAsync(orderCreated, CancellationToken.None);
        inventoryReservation.Outcome.Should().Be(InventoryReservationOutcome.Reserved);

        var inventoryReserved = ReadOutboxEnvelope<InventoryReserved>(inventoryDb.OutboxMessages.Single(message => message.EventType == nameof(InventoryReserved)));
        await CreateInventoryEventHandler(orderDb).HandleInventoryReservedAsync(inventoryReserved, CancellationToken.None);

        var paymentResult = await CreatePaymentProcessor(paymentDb, PaymentSimulationMode.AlwaysSuccess)
            .HandleInventoryReservedAsync(inventoryReserved, CancellationToken.None);
        paymentResult.Outcome.Should().Be(PaymentProcessingOutcome.Completed);

        var paymentCompleted = ReadOutboxEnvelope<PaymentCompleted>(paymentDb.OutboxMessages.Single(message => message.EventType == nameof(PaymentCompleted)));
        await CreatePaymentEventHandler(orderDb).HandlePaymentCompletedAsync(paymentCompleted, CancellationToken.None);

        var order = await orderDb.Orders.SingleAsync(order => order.Id == orderCreated.Payload.OrderId);
        order.Status.Should().Be(OrderStatus.Completed);
        paymentDb.PaymentAttempts.Should().ContainSingle(payment => payment.OrderId == order.Id && payment.Status == PaymentStatus.Completed);
        inventoryDb.Reservations.Should().ContainSingle(reservation => reservation.OrderId == order.Id && reservation.Status == InventoryReservationStatus.Reserved);
        orderDb.OutboxMessages.Should().Contain(message => message.EventType == nameof(OrderCompleted) && message.Status == OutboxMessageStatus.Pending);
    }

    [Fact]
    public async Task Always_fail_workflow_should_mark_payment_failed_and_release_inventory()
    {
        await using var orderDb = CreateOrderDbContext();
        await using var inventoryDb = CreateInventoryDbContext();
        await using var paymentDb = CreatePaymentDbContext();
        var productId = Guid.NewGuid();
        inventoryDb.Products.Add(Product.Create(productId, "SKU-PHASE4", "Phase 4 Product", 100, DateTime.UtcNow));
        await inventoryDb.SaveChangesAsync();

        var orderCreated = await CreateOrderAndReadCreatedEvent(orderDb, productId, "phase4-auto-failure");
        await CreateInventoryReservationService(inventoryDb).HandleOrderCreatedAsync(orderCreated, CancellationToken.None);
        var inventoryReserved = ReadOutboxEnvelope<InventoryReserved>(inventoryDb.OutboxMessages.Single(message => message.EventType == nameof(InventoryReserved)));
        await CreateInventoryEventHandler(orderDb).HandleInventoryReservedAsync(inventoryReserved, CancellationToken.None);

        var paymentResult = await CreatePaymentProcessor(paymentDb, PaymentSimulationMode.AlwaysFail)
            .HandleInventoryReservedAsync(inventoryReserved, CancellationToken.None);
        paymentResult.Outcome.Should().Be(PaymentProcessingOutcome.Failed);

        var paymentFailed = ReadOutboxEnvelope<PaymentFailed>(paymentDb.OutboxMessages.Single(message => message.EventType == nameof(PaymentFailed)));
        await CreatePaymentEventHandler(orderDb).HandlePaymentFailedAsync(paymentFailed, CancellationToken.None);
        await CreateInventoryReleaseService(inventoryDb).HandlePaymentFailedAsync(paymentFailed, CancellationToken.None);

        var order = await orderDb.Orders.SingleAsync(order => order.Id == orderCreated.Payload.OrderId);
        order.Status.Should().Be(OrderStatus.PaymentFailed);
        paymentDb.PaymentAttempts.Should().ContainSingle(payment => payment.OrderId == order.Id && payment.Status == PaymentStatus.Failed && payment.FailureCode == "provider_declined");
        inventoryDb.Reservations.Should().ContainSingle(reservation => reservation.OrderId == order.Id && reservation.Status == InventoryReservationStatus.Released);

        var product = await inventoryDb.Products.SingleAsync(product => product.Id == productId);
        product.AvailableStock.Should().Be(100);
        product.ReservedStock.Should().Be(0);
        inventoryDb.OutboxMessages.Should().Contain(message => message.EventType == nameof(InventoryReleased));
    }

    [Fact]
    public async Task Fail_first_then_succeed_workflow_should_retry_then_complete_order()
    {
        await using var orderDb = CreateOrderDbContext();
        await using var inventoryDb = CreateInventoryDbContext();
        await using var paymentDb = CreatePaymentDbContext();
        var productId = Guid.NewGuid();
        inventoryDb.Products.Add(Product.Create(productId, "SKU-PHASE4", "Phase 4 Product", 100, DateTime.UtcNow));
        await inventoryDb.SaveChangesAsync();

        var orderCreated = await CreateOrderAndReadCreatedEvent(orderDb, productId, "phase4-auto-retry");
        await CreateInventoryReservationService(inventoryDb).HandleOrderCreatedAsync(orderCreated, CancellationToken.None);
        var inventoryReserved = ReadOutboxEnvelope<InventoryReserved>(inventoryDb.OutboxMessages.Single(message => message.EventType == nameof(InventoryReserved)));
        await CreateInventoryEventHandler(orderDb).HandleInventoryReservedAsync(inventoryReserved, CancellationToken.None);

        var processor = CreatePaymentProcessor(paymentDb, PaymentSimulationMode.FailFirstThenSucceed);
        var firstDelivery = () => processor.HandleInventoryReservedAsync(inventoryReserved, CancellationToken.None, currentRetryCount: 0);
        await firstDelivery.Should().ThrowAsync<PaymentTransientException>()
            .Where(exception => exception.FailureCode == "provider_transient_first_attempt");
        paymentDb.ChangeTracker.Clear();
        paymentDb.PaymentAttempts.Should().BeEmpty();
        paymentDb.OutboxMessages.Should().BeEmpty();
        paymentDb.ProcessedMessages.Should().BeEmpty();

        var retryResult = await CreatePaymentProcessor(paymentDb, PaymentSimulationMode.FailFirstThenSucceed)
            .HandleInventoryReservedAsync(inventoryReserved, CancellationToken.None, currentRetryCount: 1);
        retryResult.Outcome.Should().Be(PaymentProcessingOutcome.Completed);

        var paymentCompleted = ReadOutboxEnvelope<PaymentCompleted>(paymentDb.OutboxMessages.Single(message => message.EventType == nameof(PaymentCompleted)));
        await CreatePaymentEventHandler(orderDb).HandlePaymentCompletedAsync(paymentCompleted, CancellationToken.None);

        var order = await orderDb.Orders.SingleAsync(order => order.Id == orderCreated.Payload.OrderId);
        order.Status.Should().Be(OrderStatus.Completed);
        paymentDb.PaymentAttempts.Should().ContainSingle(payment => payment.OrderId == order.Id && payment.Status == PaymentStatus.Completed);
    }



    [Fact]
    public async Task Cancel_after_inventory_reserved_should_release_inventory()
    {
        await using var orderDb = CreateOrderDbContext();
        await using var inventoryDb = CreateInventoryDbContext();
        var productId = Guid.NewGuid();
        inventoryDb.Products.Add(Product.Create(productId, "SKU-PHASE42", "Phase 4.2 Product", 100, DateTime.UtcNow));
        await inventoryDb.SaveChangesAsync();

        var orderCreated = await CreateOrderAndReadCreatedEvent(orderDb, productId, "phase42-auto-cancel");
        await CreateInventoryReservationService(inventoryDb).HandleOrderCreatedAsync(orderCreated, CancellationToken.None);
        var inventoryReserved = ReadOutboxEnvelope<InventoryReserved>(inventoryDb.OutboxMessages.Single(message => message.EventType == nameof(InventoryReserved)));
        await CreateInventoryEventHandler(orderDb).HandleInventoryReservedAsync(inventoryReserved, CancellationToken.None);

        var cancelService = CreateOrderApplicationService(orderDb, Guid.NewGuid());
        var cancelResult = await cancelService.CancelOrderAsync(orderCreated.Payload.OrderId, "customer_requested", CancellationToken.None);
        cancelResult.IsSuccess.Should().BeTrue(cancelResult.Error.Message);
        cancelResult.Value!.Status.Should().Be(OrderStatus.Cancelled);

        var orderCancelled = ReadOutboxEnvelope<OrderCancelled>(orderDb.OutboxMessages.Single(message => message.EventType == nameof(OrderCancelled)));
        var releaseResult = await CreateInventoryReleaseService(inventoryDb).HandleOrderCancelledAsync(orderCancelled, CancellationToken.None);
        releaseResult.Outcome.Should().Be(InventoryReleaseOutcome.Released);

        var order = await orderDb.Orders.SingleAsync(order => order.Id == orderCreated.Payload.OrderId);
        order.Status.Should().Be(OrderStatus.Cancelled);
        inventoryDb.Reservations.Should().ContainSingle(reservation => reservation.OrderId == order.Id && reservation.Status == InventoryReservationStatus.Released);
        var product = await inventoryDb.Products.SingleAsync(product => product.Id == productId);
        product.AvailableStock.Should().Be(100);
        product.ReservedStock.Should().Be(0);
        inventoryDb.OutboxMessages.Should().Contain(message => message.EventType == nameof(InventoryReleased));
    }


    [Fact]
    public async Task Cancelled_order_should_not_be_charged_when_stale_inventory_reserved_arrives_later()
    {
        await using var orderDb = CreateOrderDbContext();
        await using var inventoryDb = CreateInventoryDbContext();
        await using var paymentDb = CreatePaymentDbContext();
        var productId = Guid.NewGuid();
        inventoryDb.Products.Add(Product.Create(productId, "SKU-PHASE7", "Phase 7 Product", 100, DateTime.UtcNow));
        await inventoryDb.SaveChangesAsync();

        var orderCreated = await CreateOrderAndReadCreatedEvent(orderDb, productId, "phase7-auto-cancel-before-payment");
        await CreateInventoryReservationService(inventoryDb).HandleOrderCreatedAsync(orderCreated, CancellationToken.None);
        var inventoryReserved = ReadOutboxEnvelope<InventoryReserved>(inventoryDb.OutboxMessages.Single(message => message.EventType == nameof(InventoryReserved)));
        await CreateInventoryEventHandler(orderDb).HandleInventoryReservedAsync(inventoryReserved, CancellationToken.None);

        var cancelResult = await CreateOrderApplicationService(orderDb, Guid.NewGuid())
            .CancelOrderAsync(orderCreated.Payload.OrderId, "customer_requested", CancellationToken.None);
        cancelResult.IsSuccess.Should().BeTrue(cancelResult.Error.Message);

        var orderCancelled = ReadOutboxEnvelope<OrderCancelled>(orderDb.OutboxMessages.Single(message => message.EventType == nameof(OrderCancelled)));
        var paymentCancellation = await CreatePaymentCancellationHandler(paymentDb).HandleOrderCancelledAsync(orderCancelled, CancellationToken.None);
        paymentCancellation.Outcome.Should().Be(PaymentCancellationHandlingOutcome.Recorded);

        var paymentResult = await CreatePaymentProcessor(paymentDb, PaymentSimulationMode.AlwaysSuccess)
            .HandleInventoryReservedAsync(inventoryReserved, CancellationToken.None);

        paymentResult.Outcome.Should().Be(PaymentProcessingOutcome.Cancelled);
        paymentDb.PaymentAttempts.Should().BeEmpty();
        paymentDb.OutboxMessages.Should().BeEmpty();

        var releaseResult = await CreateInventoryReleaseService(inventoryDb).HandleOrderCancelledAsync(orderCancelled, CancellationToken.None);
        releaseResult.Outcome.Should().Be(InventoryReleaseOutcome.Released);

        var order = await orderDb.Orders.SingleAsync(order => order.Id == orderCreated.Payload.OrderId);
        order.Status.Should().Be(OrderStatus.Cancelled);
        inventoryDb.Reservations.Should().ContainSingle(reservation => reservation.OrderId == order.Id && reservation.Status == InventoryReservationStatus.Released);
    }

    private static async Task<EventEnvelope<OrderCreated>> CreateOrderAndReadCreatedEvent(
        OrderDbContext dbContext,
        Guid productId,
        string clientRequestPrefix)
    {
        var applicationService = CreateOrderApplicationService(dbContext, Guid.NewGuid());

        var request = new CreateOrderRequest(
            Guid.NewGuid().ToString("D"),
            $"{clientRequestPrefix}-{Guid.NewGuid():N}",
            "USD",
            [new CreateOrderItemRequest(productId.ToString("D"), 2, 12.34m)]);

        var createResult = await applicationService.CreateOrderAsync(request, CancellationToken.None);
        createResult.IsSuccess.Should().BeTrue(createResult.Error.Message);

        var outboxMessage = dbContext.OutboxMessages.Single(message => message.EventType == nameof(OrderCreated));
        outboxMessage.RoutingKey.Should().Be(EventRoutingKeys.OrderCreated);
        outboxMessage.Status.Should().Be(OutboxMessageStatus.Pending);
        return ReadOutboxEnvelope<OrderCreated>(outboxMessage);
    }

    private static EventEnvelope<TPayload> ReadOutboxEnvelope<TPayload>(OutboxMessage outboxMessage)
    {
        var envelope = JsonSerializer.Deserialize<EventEnvelope<TPayload>>(outboxMessage.Payload, JsonOptions);
        envelope.Should().NotBeNull();
        envelope!.MessageId.Should().Be(outboxMessage.MessageId);
        envelope.EventType.Should().Be(outboxMessage.EventType);
        return envelope;
    }

    private static OrderDbContext CreateOrderDbContext()
    {
        var options = new DbContextOptionsBuilder<OrderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        return new OrderDbContext(options);
    }

    private static InventoryDbContext CreateInventoryDbContext()
    {
        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        return new InventoryDbContext(options);
    }

    private static PaymentDbContext CreatePaymentDbContext()
    {
        var options = new DbContextOptionsBuilder<PaymentDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        return new PaymentDbContext(options);
    }

    private static OrderApplicationService CreateOrderApplicationService(OrderDbContext dbContext, Guid correlationId) =>
        new(
            dbContext,
            new CreateOrderRequestValidator(),
            new SystemClock(),
            new CorrelationContext { CorrelationId = correlationId },
            new NoOpOrderCache(),
            NullLogger<OrderApplicationService>.Instance);

    private static InventoryReservationService CreateInventoryReservationService(InventoryDbContext dbContext) =>
        new(dbContext, new SystemClock(), NullLogger<InventoryReservationService>.Instance);

    private static InventoryReleaseService CreateInventoryReleaseService(InventoryDbContext dbContext) =>
        new(dbContext, new SystemClock(), NullLogger<InventoryReleaseService>.Instance);

    private static InventoryEventHandler CreateInventoryEventHandler(OrderDbContext dbContext) =>
        new(dbContext, new SystemClock(), new NoOpOrderCache(), NullLogger<InventoryEventHandler>.Instance);

    private static PaymentEventHandler CreatePaymentEventHandler(OrderDbContext dbContext) =>
        new(dbContext, new SystemClock(), new NoOpOrderCache(), NullLogger<PaymentEventHandler>.Instance);

    private static PaymentProcessor CreatePaymentProcessor(PaymentDbContext dbContext, PaymentSimulationMode mode) =>
        new(
            dbContext,
            new SystemClock(),
            Options.Create(new PaymentOptions
            {
                SimulationMode = mode,
                TimeoutMilliseconds = 1,
                RandomFailurePercentage = 100
            }),
            NullLogger<PaymentProcessor>.Instance);

    private static PaymentCancellationHandler CreatePaymentCancellationHandler(PaymentDbContext dbContext) =>
        new(dbContext, new SystemClock(), NullLogger<PaymentCancellationHandler>.Instance);
}
