namespace NotificationService.Domain;

public sealed class NotificationMessage
{
    public long Id { get; private set; }
    public Guid MessageId { get; private set; }
    public Guid OrderId { get; private set; }
    public string TriggerEventType { get; private set; } = string.Empty;
    public string Channel { get; private set; } = string.Empty;
    public string Recipient { get; private set; } = string.Empty;
    public string Template { get; private set; } = string.Empty;
    public string Status { get; private set; } = string.Empty;
    public string? Error { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    private NotificationMessage()
    {
    }

    public static NotificationMessage Sent(
        Guid messageId,
        Guid orderId,
        string triggerEventType,
        string channel,
        string recipient,
        string template,
        DateTime createdAtUtc) =>
        new()
        {
            MessageId = messageId,
            OrderId = orderId,
            TriggerEventType = triggerEventType,
            Channel = channel,
            Recipient = recipient,
            Template = template,
            Status = "Sent",
            CreatedAtUtc = createdAtUtc
        };

    public static NotificationMessage Failed(
        Guid messageId,
        Guid orderId,
        string triggerEventType,
        string channel,
        string recipient,
        string template,
        string error,
        DateTime createdAtUtc) =>
        new()
        {
            MessageId = messageId,
            OrderId = orderId,
            TriggerEventType = triggerEventType,
            Channel = channel,
            Recipient = recipient,
            Template = template,
            Status = "Failed",
            Error = error,
            CreatedAtUtc = createdAtUtc
        };
}
