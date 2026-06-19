namespace BuildingBlocks.Contracts.Events;

public sealed record OrderCreated(
    Guid OrderId,
    Guid CustomerId,
    string ClientRequestId,
    IReadOnlyCollection<OrderCreatedItem> Items,
    decimal TotalAmount,
    string Currency);

public sealed record OrderCreatedItem(Guid ProductId, int Quantity, decimal UnitPrice);

public sealed record OrderCancelled(Guid OrderId, Guid CustomerId, string Reason);

public sealed record OrderCompleted(Guid OrderId, Guid CustomerId, decimal TotalAmount, string Currency);
