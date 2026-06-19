namespace BuildingBlocks.Persistence;

public enum OutboxMessageStatus
{
    Pending = 0,
    Published = 1,
    Failed = 2,
    Processing = 3
}

public sealed class OutboxMessage
{
    public long Id { get; set; }
    public Guid MessageId { get; set; }
    public Guid CorrelationId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public int EventVersion { get; set; }
    public string RoutingKey { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public DateTime OccurredOnUtc { get; set; }
    public DateTime? ProcessedOnUtc { get; set; }
    public int PublishAttempts { get; set; }
    public string? Error { get; set; }
    public OutboxMessageStatus Status { get; set; } = OutboxMessageStatus.Pending;

    public static OutboxMessage Create(
        Guid messageId,
        Guid correlationId,
        string eventType,
        int eventVersion,
        string routingKey,
        string payload,
        DateTime occurredOnUtc)
    {
        return new OutboxMessage
        {
            MessageId = messageId,
            CorrelationId = correlationId,
            EventType = eventType,
            EventVersion = eventVersion,
            RoutingKey = routingKey,
            Payload = payload,
            OccurredOnUtc = occurredOnUtc,
            Status = OutboxMessageStatus.Pending
        };
    }

    public void MarkProcessing(DateTime lockedAtUtc)
    {
        Status = OutboxMessageStatus.Processing;
        ProcessedOnUtc = lockedAtUtc;
        Error = null;
    }

    public void MarkPublished(DateTime processedOnUtc)
    {
        Status = OutboxMessageStatus.Published;
        ProcessedOnUtc = processedOnUtc;
        Error = null;
    }

    public void MarkFailed(string error)
    {
        Status = OutboxMessageStatus.Failed;
        PublishAttempts++;
        Error = error.Length <= 4000 ? error : error[..4000];
    }
}
