namespace ReportingService.Domain;

public sealed class ReportingOrder
{
    public Guid OrderId { get; private set; }
    public Guid? CustomerId { get; private set; }
    public string Status { get; private set; } = string.Empty;
    public decimal TotalAmount { get; private set; }
    public string Currency { get; private set; } = string.Empty;
    public string LastEventType { get; private set; } = string.Empty;
    public DateTime? CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }

    private ReportingOrder()
    {
    }

    public static ReportingOrder Create(Guid orderId, DateTime now) =>
        new()
        {
            OrderId = orderId,
            Status = "Unknown",
            LastEventType = "Unknown",
            UpdatedAtUtc = now
        };

    public void ApplyOrderCreated(Guid customerId, decimal totalAmount, string currency, DateTime occurredOnUtc)
    {
        CustomerId = customerId;
        TotalAmount = totalAmount;
        Currency = currency;
        Status = "Pending";
        LastEventType = "OrderCreated";
        CreatedAtUtc ??= occurredOnUtc;
        UpdatedAtUtc = occurredOnUtc;
    }

    public void ApplyStatus(string status, string eventType, DateTime occurredOnUtc)
    {
        Status = status;
        LastEventType = eventType;
        UpdatedAtUtc = occurredOnUtc;
    }

    public void ApplyPaymentCompleted(decimal amount, string currency, DateTime occurredOnUtc)
    {
        TotalAmount = amount;
        Currency = currency;
        ApplyStatus("PaymentCompleted", "PaymentCompleted", occurredOnUtc);
    }

    public void ApplyPaymentFailed(DateTime occurredOnUtc) => ApplyStatus("PaymentFailed", "PaymentFailed", occurredOnUtc);
    public void ApplyPaymentRefundRequired(DateTime occurredOnUtc) => ApplyStatus("RefundRequired", "PaymentRefundRequired", occurredOnUtc);
    public void ApplyPaymentDuplicateRejected(DateTime occurredOnUtc)
    {
        LastEventType = "PaymentDuplicateRejected";
        UpdatedAtUtc = occurredOnUtc;
    }
    public void ApplyInventoryReserved(DateTime occurredOnUtc) => ApplyStatus("InventoryReserved", "InventoryReserved", occurredOnUtc);
    public void ApplyInventoryFailed(DateTime occurredOnUtc) => ApplyStatus("InventoryFailed", "InventoryReservationFailed", occurredOnUtc);
    public void ApplyInventoryReleased(DateTime occurredOnUtc)
    {
        LastEventType = "InventoryReleased";
        UpdatedAtUtc = occurredOnUtc;
    }
    public void ApplyOrderCompleted(decimal totalAmount, string currency, DateTime occurredOnUtc)
    {
        TotalAmount = totalAmount;
        Currency = currency;
        ApplyStatus("Completed", "OrderCompleted", occurredOnUtc);
    }
    public void ApplyOrderCancelled(DateTime occurredOnUtc) => ApplyStatus("Cancelled", "OrderCancelled", occurredOnUtc);
}
