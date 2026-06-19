using System.Text;
using System.Text.Json;
using BuildingBlocks.EventBus.RabbitMQ;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace NotificationService.Messaging;

public sealed class RabbitMqNotificationEventsConsumer : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RabbitMqOptions _options;
    private readonly ILogger<RabbitMqNotificationEventsConsumer> _logger;
    private IConnection? _connection;
    private IModel? _channel;

    public RabbitMqNotificationEventsConsumer(
        IServiceScopeFactory scopeFactory,
        IOptions<RabbitMqOptions> options,
        ILogger<RabbitMqNotificationEventsConsumer> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _connection = RabbitMqConnectionFactory.Create(_options);
        _channel = _connection.CreateModel();
        RabbitMqMessageFailureHandler.DeclareReliabilityTopology(_channel);

        var arguments = new Dictionary<string, object>
        {
            ["x-dead-letter-exchange"] = RabbitMqTopology.DeadLetterExchange
        };

        _channel.QueueDeclare(RabbitMqTopology.Queues.NotificationOrderEvents, durable: true, exclusive: false, autoDelete: false, arguments: arguments);
        _channel.QueueBind(RabbitMqTopology.Queues.NotificationOrderEvents, _options.ExchangeName, "order.*");
        _channel.QueueBind(RabbitMqTopology.Queues.NotificationOrderEvents, _options.ExchangeName, "payment.*");
        _channel.QueueDeclare(RabbitMqTopology.DeadLetterQueues.NotificationFailed, durable: true, exclusive: false, autoDelete: false);
        _channel.QueueBind(RabbitMqTopology.DeadLetterQueues.NotificationFailed, RabbitMqTopology.DeadLetterExchange, "notification.#");
        _channel.BasicQos(prefetchSize: 0, prefetchCount: 10, global: false);

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.Received += HandleMessageAsync;
        _channel.BasicConsume(RabbitMqTopology.Queues.NotificationOrderEvents, autoAck: false, consumer);

        _logger.LogInformation("NotificationService is consuming events from {Queue}.", RabbitMqTopology.Queues.NotificationOrderEvents);

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task HandleMessageAsync(object sender, BasicDeliverEventArgs args)
    {
        if (_channel is null)
        {
            return;
        }

        try
        {
            var json = Encoding.UTF8.GetString(args.Body.ToArray());
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var messageId = ReadGuid(root, "messageId");
            var correlationId = ReadGuid(root, "correlationId");
            var eventType = root.GetProperty("eventType").GetString() ?? throw new JsonException("Envelope eventType is required.");
            var payload = root.GetProperty("payload");

            using var scope = _scopeFactory.CreateScope();
            var handler = scope.ServiceProvider.GetRequiredService<NotificationEventHandler>();
            await handler.HandleAsync(messageId, correlationId, eventType, payload, CancellationToken.None);
            _channel.BasicAck(args.DeliveryTag, multiple: false);
        }
        catch (JsonException ex)
        {
            RabbitMqMessageFailureHandler.DeadLetter(_channel, args, ex, "notification.failed", _logger, "Poison notification event could not be deserialized.");
        }
        catch (Exception ex)
        {
            RabbitMqMessageFailureHandler.RetryOrDeadLetter(_channel, args, ex, "notification.failed", _logger);
        }
    }

    private static Guid ReadGuid(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property) || !property.TryGetGuid(out var value))
        {
            throw new JsonException($"Envelope does not contain a valid {propertyName}.");
        }

        return value;
    }

    public override void Dispose()
    {
        _channel?.Dispose();
        _connection?.Dispose();
        base.Dispose();
    }
}
