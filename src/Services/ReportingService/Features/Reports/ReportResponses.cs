namespace ReportingService.Features.Reports;

public sealed record ReportingOrderResponse(
    Guid OrderId,
    Guid? CustomerId,
    string Status,
    decimal TotalAmount,
    string Currency,
    string LastEventType,
    DateTime? CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record ReportingEventResponse(
    long Id,
    Guid MessageId,
    Guid CorrelationId,
    Guid? OrderId,
    string EventType,
    string Source,
    DateTime OccurredOnUtc,
    DateTime RecordedAtUtc);
