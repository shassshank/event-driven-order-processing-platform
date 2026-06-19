using OrderService.Features.Orders;

namespace OrderService.Caching;

public interface IOrderCache
{
    Task<OrderResponse?> GetAsync(Guid orderId, CancellationToken cancellationToken);
    Task SetAsync(OrderResponse order, CancellationToken cancellationToken);
    Task RemoveAsync(Guid orderId, CancellationToken cancellationToken);
}
