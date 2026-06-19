namespace OrderService.Features.Orders;

public sealed record CreateOrderRequest(
    string CustomerId,
    string ClientRequestId,
    string Currency,
    IReadOnlyCollection<CreateOrderItemRequest> Items);

public sealed record CreateOrderItemRequest(string ProductId, int Quantity, decimal UnitPrice);
