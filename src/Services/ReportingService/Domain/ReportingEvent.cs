namespace ReportingService.Domain;

public sealed class ReportingEvent
{
    public long Id { get; private set; }
    public Guid MessageId { get; private set; }
    public Guid CorrelationId { get; private set; }
    public Guid? OrderId { get; private set; }
    public string EventType { get; private set; } = string.Empty;
    public string Source { get; private set; } = string.Empty;
    public string Payload { get; private set; } = string.Empty;
    public DateTime OccurredOnUtc { get; private set; }
    public DateTime RecordedAtUtc { get; private set; }

    private ReportingEvent()
    {
    }

    public static ReportingEvent Create(
        Guid messageId,
        Guid correlationId,
        Guid? orderId,
        string eventType,
        string source,
        string payload,
        DateTime occurredOnUtc,
        DateTime recordedAtUtc) =>
        new()
        {
            MessageId = messageId,
            CorrelationId = correlationId,
            OrderId = orderId,
            EventType = eventType,
            Source = source,
            Payload = payload,
            OccurredOnUtc = occurredOnUtc,
            RecordedAtUtc = recordedAtUtc
        };
}
