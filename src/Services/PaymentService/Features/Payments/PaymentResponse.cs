namespace PaymentService.Features.Payments;

public sealed record PaymentResponse(
    Guid Id,
    Guid OrderId,
    decimal Amount,
    string Currency,
    string Status,
    string? ProviderTransactionId,
    string? FailureCode,
    string? FailureReason,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record PaymentCancellationResponse(
    Guid OrderId,
    Guid CustomerId,
    string Reason,
    string Status,
    DateTime CancelledAtUtc,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);
