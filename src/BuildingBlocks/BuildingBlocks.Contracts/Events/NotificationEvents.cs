namespace BuildingBlocks.Contracts.Events;

public sealed record NotificationSent(Guid OrderId, string Channel, string Recipient, string Template);

public sealed record NotificationFailed(Guid OrderId, string Channel, string Recipient, string Template, string Reason);
