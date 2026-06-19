namespace InventoryService.Domain;

public sealed class CancelledOrder
{
    private CancelledOrder()
    {
    }

    private CancelledOrder(Guid orderId, Guid customerId, string reason, Guid messageId, DateTime cancelledAtUtc)
    {
        OrderId = orderId;
        CustomerId = customerId;
        Reason = reason;
        MessageId = messageId;
        CancelledAtUtc = cancelledAtUtc;
    }

    public Guid OrderId { get; private set; }
    public Guid CustomerId { get; private set; }
    public string Reason { get; private set; } = string.Empty;
    public Guid MessageId { get; private set; }
    public DateTime CancelledAtUtc { get; private set; }

    public static CancelledOrder Create(Guid orderId, Guid customerId, string reason, Guid messageId, DateTime cancelledAtUtc) =>
        new(orderId, customerId, reason, messageId, cancelledAtUtc);
}
