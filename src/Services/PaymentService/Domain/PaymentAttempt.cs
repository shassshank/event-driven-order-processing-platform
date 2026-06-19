namespace PaymentService.Domain;

public sealed class PaymentAttempt
{
    private PaymentAttempt()
    {
    }

    private PaymentAttempt(Guid id, Guid orderId, decimal amount, string currency, DateTime createdAtUtc)
    {
        Id = id;
        OrderId = orderId;
        Amount = amount;
        Currency = currency.ToUpperInvariant();
        Status = PaymentStatus.Pending;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; }
    public Guid OrderId { get; private set; }
    public decimal Amount { get; private set; }
    public string Currency { get; private set; } = "USD";
    public PaymentStatus Status { get; private set; }
    public string? ProviderTransactionId { get; private set; }
    public string? FailureCode { get; private set; }
    public string? FailureReason { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }

    public static PaymentAttempt Create(Guid orderId, decimal amount, string currency, DateTime createdAtUtc) =>
        new(Guid.NewGuid(), orderId, amount, currency, createdAtUtc);

    public void Complete(string providerTransactionId, DateTime utcNow)
    {
        if (Status != PaymentStatus.Pending)
        {
            return;
        }

        Status = PaymentStatus.Completed;
        ProviderTransactionId = providerTransactionId;
        UpdatedAtUtc = utcNow;
    }

    public void Fail(string failureCode, string failureReason, DateTime utcNow)
    {
        if (Status != PaymentStatus.Pending)
        {
            return;
        }

        Status = PaymentStatus.Failed;
        FailureCode = failureCode;
        FailureReason = failureReason;
        UpdatedAtUtc = utcNow;
    }
}
