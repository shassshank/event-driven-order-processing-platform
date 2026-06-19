using BuildingBlocks.EventBus.RabbitMQ;
using BuildingBlocks.Persistence;
using BuildingBlocks.SharedKernel;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderService.Outbox;
using OrderService.Persistence;
using Xunit;

namespace OrderService.UnitTests;

public sealed class OutboxPublisherTests
{
    [Fact]
    public async Task Should_mark_message_as_published_after_successful_publish()
    {
        await using var db = CreateDbContext();
        var message = PendingMessage();
        db.OutboxMessages.Add(message);
        await db.SaveChangesAsync();

        var bus = new RecordingEventBus();
        var publisher = new OutboxPublisher(
            db,
            bus,
            new SystemClock(),
            Options.Create(new OutboxOptions { BatchSize = 10 }),
            NullLogger<OutboxPublisher>.Instance);

        var published = await publisher.PublishPendingMessagesAsync(CancellationToken.None);

        published.Should().Be(1);
        bus.PublishedMessages.Should().ContainSingle(messageId => messageId == message.MessageId);
        message.Status.Should().Be(OutboxMessageStatus.Published);
        message.ProcessedOnUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task Should_keep_message_failed_when_publish_fails()
    {
        await using var db = CreateDbContext();
        var message = PendingMessage();
        db.OutboxMessages.Add(message);
        await db.SaveChangesAsync();

        var publisher = new OutboxPublisher(
            db,
            new ThrowingEventBus(),
            new SystemClock(),
            Options.Create(new OutboxOptions { BatchSize = 10 }),
            NullLogger<OutboxPublisher>.Instance);

        var published = await publisher.PublishPendingMessagesAsync(CancellationToken.None);

        published.Should().Be(0);
        message.Status.Should().Be(OutboxMessageStatus.Failed);
        message.PublishAttempts.Should().Be(1);
        message.Error.Should().Contain("broker unavailable");
    }

    private static OrderDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<OrderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        return new OrderDbContext(options);
    }

    private static OutboxMessage PendingMessage()
    {
        var messageId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        var payload = $$"""
        {
          "messageId": "{{messageId}}",
          "correlationId": "{{correlationId}}",
          "causationId": "{{messageId}}",
          "eventType": "OrderCreated",
          "eventVersion": 1,
          "occurredOnUtc": "{{DateTime.UtcNow:O}}",
          "source": "OrderService",
          "payload": {}
        }
        """;

        return OutboxMessage.Create(
            messageId,
            correlationId,
            "OrderCreated",
            1,
            "order.created",
            payload,
            DateTime.UtcNow);
    }

    private sealed class RecordingEventBus : IEventBus
    {
        public List<Guid> PublishedMessages { get; } = [];

        public Task PublishAsync<TPayload>(BuildingBlocks.Contracts.EventEnvelope<TPayload> envelope, string routingKey, CancellationToken cancellationToken = default)
        {
            PublishedMessages.Add(envelope.MessageId);
            return Task.CompletedTask;
        }

        public Task PublishRawAsync(string envelopeJson, string routingKey, Guid messageId, Guid correlationId, string eventType, int eventVersion, DateTime occurredOnUtc, CancellationToken cancellationToken = default, Guid? causationId = null)
        {
            PublishedMessages.Add(messageId);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingEventBus : IEventBus
    {
        public Task PublishAsync<TPayload>(BuildingBlocks.Contracts.EventEnvelope<TPayload> envelope, string routingKey, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("broker unavailable");

        public Task PublishRawAsync(string envelopeJson, string routingKey, Guid messageId, Guid correlationId, string eventType, int eventVersion, DateTime occurredOnUtc, CancellationToken cancellationToken = default, Guid? causationId = null) =>
            throw new InvalidOperationException("broker unavailable");
    }
}
