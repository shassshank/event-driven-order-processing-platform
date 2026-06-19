namespace BuildingBlocks.Contracts;

public sealed record EventEnvelope<TPayload>(
    Guid MessageId,
    Guid CorrelationId,
    Guid CausationId,
    string EventType,
    int EventVersion,
    DateTime OccurredOnUtc,
    string Source,
    TPayload Payload);

public static class EventEnvelope
{
    public static EventEnvelope<TPayload> Create<TPayload>(
        TPayload payload,
        Guid correlationId,
        Guid? causationId,
        string source,
        DateTime occurredOnUtc,
        int eventVersion = 1)
    {
        return new EventEnvelope<TPayload>(
            Guid.NewGuid(),
            correlationId,
            causationId ?? correlationId,
            typeof(TPayload).Name,
            eventVersion,
            occurredOnUtc,
            source,
            payload);
    }
}
