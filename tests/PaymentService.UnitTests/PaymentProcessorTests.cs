using BuildingBlocks.Contracts;
using BuildingBlocks.Contracts.Events;
using BuildingBlocks.SharedKernel;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PaymentService.Domain;
using PaymentService.Messaging;
using PaymentService.Persistence;
using Xunit;

namespace PaymentServiceUnitTests;

public sealed class PaymentProcessorTests
{
    [Fact]
    public async Task Fail_first_then_succeed_should_throw_transient_exception_on_first_delivery()
    {
        await using var db = CreateDbContext();
        var processor = CreateProcessor(db, PaymentSimulationMode.FailFirstThenSucceed);
        var envelope = CreateInventoryReservedEnvelope();

        var act = () => processor.HandleInventoryReservedAsync(envelope, CancellationToken.None, currentRetryCount: 0);

        await act.Should().ThrowAsync<PaymentTransientException>()
            .WithMessage("*first attempt*");
        db.ChangeTracker.Clear();
        db.PaymentAttempts.Should().BeEmpty();
        db.ProcessedMessages.Should().BeEmpty();
        db.OutboxMessages.Should().BeEmpty();
    }

    [Fact]
    public async Task Fail_first_then_succeed_should_complete_after_retry_header_is_present()
    {
        await using var db = CreateDbContext();
        var processor = CreateProcessor(db, PaymentSimulationMode.FailFirstThenSucceed);
        var envelope = CreateInventoryReservedEnvelope();

        var result = await processor.HandleInventoryReservedAsync(envelope, CancellationToken.None, currentRetryCount: 1);

        result.Outcome.Should().Be(PaymentProcessingOutcome.Completed);
        db.PaymentAttempts.Should().ContainSingle(payment => payment.Status == PaymentStatus.Completed);
        db.ProcessedMessages.Should().ContainSingle(message => message.MessageId == envelope.MessageId);
        db.OutboxMessages.Should().ContainSingle(message => message.EventType == nameof(PaymentCompleted)
            && message.RoutingKey == EventRoutingKeys.PaymentCompleted);
    }

    [Fact]
    public async Task Timeout_mode_should_throw_transient_exception_without_persisting_payment()
    {
        await using var db = CreateDbContext();
        var processor = CreateProcessor(db, PaymentSimulationMode.Timeout, timeoutMilliseconds: 1);
        var envelope = CreateInventoryReservedEnvelope();

        var act = () => processor.HandleInventoryReservedAsync(envelope, CancellationToken.None, currentRetryCount: 0);

        await act.Should().ThrowAsync<PaymentTransientException>()
            .Where(ex => ex.FailureCode == "provider_timeout");
        db.ChangeTracker.Clear();
        db.PaymentAttempts.Should().BeEmpty();
        db.ProcessedMessages.Should().BeEmpty();
        db.OutboxMessages.Should().BeEmpty();
    }


    [Fact]
    public async Task Should_skip_payment_when_order_was_cancelled_before_inventory_reserved_arrives()
    {
        await using var db = CreateDbContext();
        var envelope = CreateInventoryReservedEnvelope();
        db.CancelledOrders.Add(PaymentCancelledOrder.Record(
            envelope.Payload.OrderId,
            Guid.NewGuid(),
            "customer_requested",
            DateTime.UtcNow));
        await db.SaveChangesAsync();
        var processor = CreateProcessor(db, PaymentSimulationMode.AlwaysSuccess);

        var result = await processor.HandleInventoryReservedAsync(envelope, CancellationToken.None);

        result.Outcome.Should().Be(PaymentProcessingOutcome.Cancelled);
        db.PaymentAttempts.Should().BeEmpty();
        db.OutboxMessages.Should().BeEmpty();
        db.ProcessedMessages.Should().ContainSingle(message => message.MessageId == envelope.MessageId);
    }


    [Fact]
    public async Task Should_skip_same_inventory_reserved_message_when_already_processed()
    {
        await using var db = CreateDbContext();
        var processor = CreateProcessor(db, PaymentSimulationMode.AlwaysSuccess);
        var envelope = CreateInventoryReservedEnvelope();

        await processor.HandleInventoryReservedAsync(envelope, CancellationToken.None);
        var duplicate = await processor.HandleInventoryReservedAsync(envelope, CancellationToken.None);

        duplicate.Outcome.Should().Be(PaymentProcessingOutcome.Duplicate);
        db.PaymentAttempts.Should().ContainSingle(payment => payment.OrderId == envelope.Payload.OrderId);
        db.ProcessedMessages.Should().ContainSingle(message =>
            message.MessageId == envelope.MessageId &&
            message.ConsumerName == PaymentProcessor.ConsumerName);
        db.OutboxMessages.Should().ContainSingle(message => message.EventType == nameof(PaymentCompleted));
    }

    [Fact]
    public async Task Should_reject_duplicate_payment_request_when_same_order_has_existing_payment_with_different_message_id()
    {
        await using var db = CreateDbContext();
        var processor = CreateProcessor(db, PaymentSimulationMode.AlwaysSuccess);
        var first = CreateInventoryReservedEnvelope();
        var second = CreateInventoryReservedEnvelope(first.Payload.OrderId);

        await processor.HandleInventoryReservedAsync(first, CancellationToken.None);
        var duplicateLogicalEvent = await processor.HandleInventoryReservedAsync(second, CancellationToken.None);

        duplicateLogicalEvent.Outcome.Should().Be(PaymentProcessingOutcome.DuplicateRejected);
        db.PaymentAttempts.Should().ContainSingle(payment => payment.OrderId == first.Payload.OrderId);
        db.ProcessedMessages.Count().Should().Be(2);
        db.OutboxMessages.Should().ContainSingle(message => message.EventType == nameof(PaymentCompleted));
        db.OutboxMessages.Should().ContainSingle(message =>
            message.EventType == nameof(PaymentDuplicateRejected) &&
            message.RoutingKey == EventRoutingKeys.PaymentDuplicateRejected);
    }


    [Fact]
    public async Task Should_reject_duplicate_payment_request_after_failed_payment_without_retrying_provider()
    {
        await using var db = CreateDbContext();
        var processor = CreateProcessor(db, PaymentSimulationMode.AlwaysFail);
        var first = CreateInventoryReservedEnvelope();
        var second = CreateInventoryReservedEnvelope(first.Payload.OrderId);

        var failed = await processor.HandleInventoryReservedAsync(first, CancellationToken.None);
        var duplicateLogicalEvent = await processor.HandleInventoryReservedAsync(second, CancellationToken.None);

        failed.Outcome.Should().Be(PaymentProcessingOutcome.Failed);
        duplicateLogicalEvent.Outcome.Should().Be(PaymentProcessingOutcome.DuplicateRejected);
        db.PaymentAttempts.Should().ContainSingle(payment =>
            payment.OrderId == first.Payload.OrderId &&
            payment.Status == PaymentStatus.Failed);
        db.OutboxMessages.Should().ContainSingle(message => message.EventType == nameof(PaymentFailed));
        db.OutboxMessages.Should().ContainSingle(message => message.EventType == nameof(PaymentDuplicateRejected));
    }

    private static PaymentDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<PaymentDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        return new PaymentDbContext(options);
    }

    private static PaymentProcessor CreateProcessor(
        PaymentDbContext dbContext,
        PaymentSimulationMode mode,
        int timeoutMilliseconds = 250) =>
        new(
            dbContext,
            new SystemClock(),
            Options.Create(new PaymentOptions
            {
                SimulationMode = mode,
                TimeoutMilliseconds = timeoutMilliseconds,
                RandomFailurePercentage = 100
            }),
            NullLogger<PaymentProcessor>.Instance);

    private static EventEnvelope<InventoryReserved> CreateInventoryReservedEnvelope(Guid? orderId = null)
    {
        var payload = new InventoryReserved(
            orderId ?? Guid.NewGuid(),
            24.68m,
            "USD",
            [new InventoryReservedItem(Guid.NewGuid(), 2)]);

        return EventEnvelope.Create(payload, Guid.NewGuid(), Guid.NewGuid(), "InventoryService", DateTime.UtcNow);
    }
}
