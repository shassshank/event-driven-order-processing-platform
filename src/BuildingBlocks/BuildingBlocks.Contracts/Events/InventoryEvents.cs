namespace BuildingBlocks.Contracts.Events;

public sealed record InventoryReserved(
    Guid OrderId,
    decimal TotalAmount,
    string Currency,
    IReadOnlyCollection<InventoryReservedItem> Items);

public sealed record InventoryReservedItem(Guid ProductId, int Quantity);

public sealed record InventoryReservationFailed(Guid OrderId, string Reason, IReadOnlyCollection<InventoryFailureItem> FailedItems);

public sealed record InventoryFailureItem(Guid ProductId, int RequestedQuantity, int AvailableQuantity, string Reason);

public sealed record InventoryReleased(Guid OrderId, IReadOnlyCollection<InventoryReleasedItem> Items);

public sealed record InventoryReleasedItem(Guid ProductId, int Quantity);
