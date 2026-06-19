namespace BuildingBlocks.EventBus.RabbitMQ;

public sealed class RabbitMqOptions
{
    public string HostName { get; init; } = "localhost";
    public int Port { get; init; } = 5672;
    public string VirtualHost { get; init; } = "/";
    public string UserName { get; init; } = "guest";
    public string Password { get; init; } = "guest";
    public string ExchangeName { get; init; } = RabbitMqTopology.Exchange;
    public int PublisherConfirmTimeoutSeconds { get; init; } = 5;
}
