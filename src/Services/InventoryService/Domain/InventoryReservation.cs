namespace InventoryService.Domain;

public enum InventoryReservationStatus
{
    Reserved = 0,
    Failed = 1,
    Released = 2
}

public sealed class InventoryReservation
{
    private InventoryReservation()
    {
    }

    private InventoryReservation(Guid orderId, Guid productId, int quantity, InventoryReservationStatus status, DateTime createdAtUtc)
    {
        OrderId = orderId;
        ProductId = productId;
        Quantity = quantity;
        Status = status;
        CreatedAtUtc = createdAtUtc;
    }

    public Guid OrderId { get; private set; }
    public Guid ProductId { get; private set; }
    public int Quantity { get; private set; }
    public InventoryReservationStatus Status { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    public static InventoryReservation Reserved(Guid orderId, Guid productId, int quantity, DateTime createdAtUtc) =>
        new(orderId, productId, quantity, InventoryReservationStatus.Reserved, createdAtUtc);

    public static InventoryReservation Failed(Guid orderId, Guid productId, int quantity, DateTime createdAtUtc) =>
        new(orderId, productId, quantity, InventoryReservationStatus.Failed, createdAtUtc);

    public void Release()
    {
        if (Status == InventoryReservationStatus.Released)
        {
            return;
        }

        if (Status != InventoryReservationStatus.Reserved)
        {
            throw new InvalidOperationException($"Reservation for order '{OrderId}' and product '{ProductId}' cannot be released from status '{Status}'.");
        }

        Status = InventoryReservationStatus.Released;
    }
}
