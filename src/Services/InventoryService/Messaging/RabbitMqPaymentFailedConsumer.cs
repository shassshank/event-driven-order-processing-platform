using System.Text;
using System.Text.Json;
using BuildingBlocks.Contracts;
using BuildingBlocks.Contracts.Events;
using BuildingBlocks.EventBus.RabbitMQ;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace InventoryService.Messaging;

public sealed class RabbitMqPaymentFailedConsumer : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RabbitMqOptions _options;
    private readonly ILogger<RabbitMqPaymentFailedConsumer> _logger;
    private IConnection? _connection;
    private IModel? _channel;

    public RabbitMqPaymentFailedConsumer(
        IServiceScopeFactory scopeFactory,
        IOptions<RabbitMqOptions> options,
        ILogger<RabbitMqPaymentFailedConsumer> logger)
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
            RabbitMqTopology.Queues.InventoryPaymentFailed,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: arguments);
        _channel.QueueBind(
            RabbitMqTopology.Queues.InventoryPaymentFailed,
            _options.ExchangeName,
            EventRoutingKeys.PaymentFailed);

        _channel.BasicQos(prefetchSize: 0, prefetchCount: 10, global: false);

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.Received += HandleMessageAsync;

        _channel.BasicConsume(
            queue: RabbitMqTopology.Queues.InventoryPaymentFailed,
            autoAck: false,
            consumer: consumer);

        _logger.LogInformation(
            "InventoryService is consuming {Queue} with routing key {RoutingKey}",
            RabbitMqTopology.Queues.InventoryPaymentFailed,
            EventRoutingKeys.PaymentFailed);

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
            var envelope = JsonSerializer.Deserialize<EventEnvelope<PaymentFailed>>(json, JsonOptions)
                ?? throw new JsonException("PaymentFailed envelope could not be deserialized.");

            if (!string.Equals(envelope.EventType, nameof(PaymentFailed), StringComparison.Ordinal))
            {
                throw new JsonException($"Unexpected event type '{envelope.EventType}'.");
            }

            using var scope = _scopeFactory.CreateScope();
            var handler = scope.ServiceProvider.GetRequiredService<InventoryReleaseService>();
            await handler.HandlePaymentFailedAsync(envelope, CancellationToken.None);

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
                "Poison PaymentFailed message could not be deserialized.");
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
