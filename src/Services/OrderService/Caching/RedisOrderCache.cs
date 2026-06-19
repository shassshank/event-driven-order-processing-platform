using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using OrderService.Features.Orders;

namespace OrderService.Caching;

public sealed class RedisOrderCache : IOrderCache
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IDistributedCache _cache;
    private readonly RedisOrderCacheOptions _options;
    private readonly ILogger<RedisOrderCache> _logger;

    public RedisOrderCache(IDistributedCache cache, IOptions<RedisOrderCacheOptions> options, ILogger<RedisOrderCache> logger)
    {
        _cache = cache;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<OrderResponse?> GetAsync(Guid orderId, CancellationToken cancellationToken)
    {
        try
        {
            var json = await _cache.GetStringAsync(Key(orderId), cancellationToken);
            return string.IsNullOrWhiteSpace(json)
                ? null
                : JsonSerializer.Deserialize<OrderResponse>(json, JsonOptions);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Redis cache read failed for order {OrderId}. Falling back to PostgreSQL.", orderId);
            return null;
        }
    }

    public async Task SetAsync(OrderResponse order, CancellationToken cancellationToken)
    {
        try
        {
            var json = JsonSerializer.Serialize(order, JsonOptions);
            await _cache.SetStringAsync(
                Key(order.Id),
                json,
                new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(_options.AbsoluteExpirationSeconds)
                },
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Redis cache write failed for order {OrderId}.", order.Id);
        }
    }

    public async Task RemoveAsync(Guid orderId, CancellationToken cancellationToken)
    {
        try
        {
            await _cache.RemoveAsync(Key(orderId), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Redis cache invalidation failed for order {OrderId}.", orderId);
        }
    }

    private static string Key(Guid orderId) => $"orders:{orderId:D}";
}

public sealed class RedisOrderCacheOptions
{
    public int AbsoluteExpirationSeconds { get; init; } = 30;
}
