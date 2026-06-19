using BuildingBlocks.SharedKernel;

namespace OrderService.Features.Orders;

public interface IOrderApplicationService
{
    Task<Result<OrderResponse>> CreateOrderAsync(CreateOrderRequest request, CancellationToken cancellationToken);
    Task<Result<OrderResponse>> GetOrderByIdAsync(Guid orderId, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<OrderResponse>> GetOrdersAsync(CancellationToken cancellationToken);
    Task<Result<OrderResponse>> CancelOrderAsync(Guid orderId, string reason, CancellationToken cancellationToken);
}
