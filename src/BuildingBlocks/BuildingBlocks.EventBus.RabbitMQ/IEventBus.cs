using BuildingBlocks.Contracts;

namespace BuildingBlocks.EventBus.RabbitMQ;

public interface IEventBus
{
    Task PublishAsync<TPayload>(
        EventEnvelope<TPayload> envelope,
        string routingKey,
        CancellationToken cancellationToken = default);

    Task PublishRawAsync(
        string envelopeJson,
        string routingKey,
        Guid messageId,
        Guid correlationId,
        string eventType,
        int eventVersion,
        DateTime occurredOnUtc,
        CancellationToken cancellationToken = default,
        Guid? causationId = null);
}
