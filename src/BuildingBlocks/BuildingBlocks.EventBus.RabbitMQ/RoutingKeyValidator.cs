using BuildingBlocks.Contracts;

namespace BuildingBlocks.EventBus.RabbitMQ;

public static class RoutingKeyValidator
{
    public static void ThrowIfUnsupported(string routingKey)
    {
        if (!EventRoutingKeys.All.Contains(routingKey))
        {
            throw new InvalidOperationException($"Routing key '{routingKey}' is not supported by the order platform contract.");
        }
    }
}
