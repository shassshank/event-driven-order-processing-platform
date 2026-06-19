using System.Text;
using System.Text.Json;
using BuildingBlocks.Contracts;
using BuildingBlocks.Contracts.Events;
using BuildingBlocks.EventBus.RabbitMQ;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace OrderService.Messaging;

public sealed class RabbitMqInventoryEventsConsumer : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RabbitMqOptions _options;
    private readonly ILogger<RabbitMqInventoryEventsConsumer> _logger;
    private IConnection? _connection;
    private IModel? _channel;

    public RabbitMqInventoryEventsConsumer(
        IServiceScopeFactory scopeFactory,
        IOptions<RabbitMqOptions> options,
        ILogger<RabbitMqInventoryEventsConsumer> logger)
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
            ["x-dead-letter-exchange"] = RabbitMqTopology.DeadLetterExchange,
            ["x-dead-letter-routing-key"] = "inventory.failed"
        };

        _channel.QueueDeclare(
            RabbitMqTopology.Queues.OrderInventoryEvents,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: arguments);
        _channel.QueueBind(
            RabbitMqTopology.Queues.OrderInventoryEvents,
            _options.ExchangeName,
            EventRoutingKeys.InventoryReserved);
        _channel.QueueBind(
            RabbitMqTopology.Queues.OrderInventoryEvents,
            _options.ExchangeName,
            EventRoutingKeys.InventoryReservationFailed);

        _channel.BasicQos(prefetchSize: 0, prefetchCount: 10, global: false);

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.Received += HandleMessageAsync;

        _channel.BasicConsume(
            queue: RabbitMqTopology.Queues.OrderInventoryEvents,
            autoAck: false,
            consumer: consumer);

        _logger.LogInformation(
            "OrderService is consuming inventory events from queue {Queue}",
            RabbitMqTopology.Queues.OrderInventoryEvents);

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown path.
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
            if (!document.RootElement.TryGetProperty("eventType", out var eventTypeElement))
            {
                throw new JsonException("Inventory event envelope does not contain eventType.");
            }

            var eventType = eventTypeElement.GetString();
            using var scope = _scopeFactory.CreateScope();
            var handler = scope.ServiceProvider.GetRequiredService<InventoryEventHandler>();

            switch (eventType)
            {
                case nameof(InventoryReserved):
                {
                    var envelope = JsonSerializer.Deserialize<EventEnvelope<InventoryReserved>>(json, JsonOptions)
                        ?? throw new JsonException("InventoryReserved envelope could not be deserialized.");
                    await handler.HandleInventoryReservedAsync(envelope, CancellationToken.None);
                    break;
                }
                case nameof(InventoryReservationFailed):
                {
                    var envelope = JsonSerializer.Deserialize<EventEnvelope<InventoryReservationFailed>>(json, JsonOptions)
                        ?? throw new JsonException("InventoryReservationFailed envelope could not be deserialized.");
                    await handler.HandleInventoryReservationFailedAsync(envelope, CancellationToken.None);
                    break;
                }
                default:
                    throw new JsonException($"Unexpected inventory event type '{eventType}'.");
            }

            _channel.BasicAck(args.DeliveryTag, multiple: false);
        }
        catch (JsonException ex)
        {
            RabbitMqMessageFailureHandler.DeadLetter(
                _channel,
                args,
                ex,
                "inventory.failed",
                _logger,
                "Poison inventory event could not be deserialized.");
        }
        catch (Exception ex)
        {
            RabbitMqMessageFailureHandler.RetryOrDeadLetter(
                _channel,
                args,
                ex,
                "inventory.failed",
                _logger);
        }
    }

    public override void Dispose()
    {
        _channel?.Dispose();
        _connection?.Dispose();
        base.Dispose();
    }
}
