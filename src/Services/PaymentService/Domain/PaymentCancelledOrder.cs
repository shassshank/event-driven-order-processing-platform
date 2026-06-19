namespace PaymentService.Domain;

public sealed class PaymentCancelledOrder
{
    private PaymentCancelledOrder()
    {
    }

    private PaymentCancelledOrder(Guid orderId, Guid customerId, string reason, DateTime cancelledAtUtc)
    {
        OrderId = orderId;
        CustomerId = customerId;
        Reason = string.IsNullOrWhiteSpace(reason) ? "unspecified" : reason.Trim();
        Status = PaymentCancellationStatus.Recorded;
        CancelledAtUtc = cancelledAtUtc;
        CreatedAtUtc = cancelledAtUtc;
        UpdatedAtUtc = cancelledAtUtc;
    }

    public Guid OrderId { get; private set; }
    public Guid CustomerId { get; private set; }
    public string Reason { get; private set; } = string.Empty;
    public PaymentCancellationStatus Status { get; private set; }
    public DateTime CancelledAtUtc { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }

    public static PaymentCancelledOrder Record(Guid orderId, Guid customerId, string reason, DateTime cancelledAtUtc) =>
        new(orderId, customerId, reason, cancelledAtUtc);

    public void MarkRefundRequired(DateTime utcNow)
    {
        if (Status == PaymentCancellationStatus.RefundRequired)
        {
            return;
        }

        Status = PaymentCancellationStatus.RefundRequired;
        UpdatedAtUtc = utcNow;
    }
}
