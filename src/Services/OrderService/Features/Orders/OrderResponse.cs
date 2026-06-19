using OrderService.Domain;

namespace OrderService.Features.Orders;

public sealed record OrderResponse(
    Guid Id,
    Guid CustomerId,
    string ClientRequestId,
    OrderStatus Status,
    decimal TotalAmount,
    string Currency,
    DateTime CreatedAtUtc,
    IReadOnlyCollection<OrderItemResponse> Items);

public sealed record OrderItemResponse(Guid ProductId, int Quantity, decimal UnitPrice, decimal LineTotal);
