using OrderService.Features.Orders;

namespace OrderService.Caching;

public sealed class NoOpOrderCache : IOrderCache
{
    public Task<OrderResponse?> GetAsync(Guid orderId, CancellationToken cancellationToken) => Task.FromResult<OrderResponse?>(null);
    public Task SetAsync(OrderResponse order, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task RemoveAsync(Guid orderId, CancellationToken cancellationToken) => Task.CompletedTask;
}
