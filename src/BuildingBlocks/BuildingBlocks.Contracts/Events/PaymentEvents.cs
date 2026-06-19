namespace BuildingBlocks.Contracts.Events;

public sealed record PaymentCompleted(
    Guid OrderId,
    Guid PaymentId,
    string ProviderTransactionId,
    decimal Amount,
    string Currency);

public sealed record PaymentFailed(
    Guid OrderId,
    Guid PaymentId,
    string FailureCode,
    string FailureReason,
    decimal Amount,
    string Currency);

public sealed record PaymentRefundRequired(
    Guid OrderId,
    Guid PaymentId,
    string ProviderTransactionId,
    decimal Amount,
    string Currency,
    string Reason);

public sealed record PaymentDuplicateRejected(
    Guid OrderId,
    Guid ExistingPaymentId,
    string? ExistingProviderTransactionId,
    string ExistingPaymentStatus,
    decimal Amount,
    string Currency,
    string Reason);
