using RabbitMQ.Client;

namespace BuildingBlocks.EventBus.RabbitMQ;

public static class RabbitMqConnectionFactory
{
    public static IConnection Create(RabbitMqOptions options)
    {
        var factory = new ConnectionFactory
        {
            HostName = options.HostName,
            Port = options.Port,
            VirtualHost = options.VirtualHost,
            UserName = options.UserName,
            Password = options.Password,
            DispatchConsumersAsync = true,
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true,
            RequestedHeartbeat = TimeSpan.FromSeconds(30)
        };

        return factory.CreateConnection("order-platform");
    }
}
