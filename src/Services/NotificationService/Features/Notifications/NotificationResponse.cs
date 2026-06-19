namespace NotificationService.Features.Notifications;

public sealed record NotificationResponse(
    long Id,
    Guid MessageId,
    Guid OrderId,
    string TriggerEventType,
    string Channel,
    string Recipient,
    string Template,
    string Status,
    string? Error,
    DateTime CreatedAtUtc);
