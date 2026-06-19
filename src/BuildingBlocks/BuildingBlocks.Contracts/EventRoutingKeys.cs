namespace BuildingBlocks.Contracts;

public static class EventRoutingKeys
{
    public const string OrderCreated = "order.created";
    public const string OrderCancelled = "order.cancelled";
    public const string OrderCompleted = "order.completed";
    public const string InventoryReserved = "inventory.reserved";
    public const string InventoryReservationFailed = "inventory.reservation_failed";
    public const string InventoryReleased = "inventory.released";
    public const string PaymentCompleted = "payment.completed";
    public const string PaymentFailed = "payment.failed";
    public const string PaymentRefundRequired = "payment.refund_required";
    public const string PaymentDuplicateRejected = "payment.duplicate_rejected";
    public const string NotificationSent = "notification.sent";
    public const string NotificationFailed = "notification.failed";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        OrderCreated,
        OrderCancelled,
        OrderCompleted,
        InventoryReserved,
        InventoryReservationFailed,
        InventoryReleased,
        PaymentCompleted,
        PaymentFailed,
        PaymentRefundRequired,
        PaymentDuplicateRejected,
        NotificationSent,
        NotificationFailed
    };
}
