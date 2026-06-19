using System.Text;
using System.Text.Json;
using BuildingBlocks.Contracts;
using BuildingBlocks.Contracts.Events;
using BuildingBlocks.EventBus.RabbitMQ;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace PaymentService.Messaging;

public sealed class RabbitMqInventoryReservedConsumer : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RabbitMqOptions _options;
    private readonly ILogger<RabbitMqInventoryReservedConsumer> _logger;
    private IConnection? _connection;
    private IModel? _channel;

    public RabbitMqInventoryReservedConsumer(
        IServiceScopeFactory scopeFactory,
        IOptions<RabbitMqOptions> options,
        ILogger<RabbitMqInventoryReservedConsumer> logger)
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
            ["x-dead-letter-routing-key"] = "payment.failed"
        };

        _channel.QueueDeclare(
            RabbitMqTopology.Queues.PaymentInventoryReserved,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: arguments);
        _channel.QueueBind(
            RabbitMqTopology.Queues.PaymentInventoryReserved,
            _options.ExchangeName,
            EventRoutingKeys.InventoryReserved);
        _channel.QueueDeclare(
            RabbitMqTopology.DeadLetterQueues.PaymentFailed,
            durable: true,
            exclusive: false,
            autoDelete: false);
        _channel.QueueBind(
            RabbitMqTopology.DeadLetterQueues.PaymentFailed,
            RabbitMqTopology.DeadLetterExchange,
            "payment.#");

        _channel.BasicQos(prefetchSize: 0, prefetchCount: 10, global: false);

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.Received += HandleMessageAsync;

        _channel.BasicConsume(
            queue: RabbitMqTopology.Queues.PaymentInventoryReserved,
            autoAck: false,
            consumer: consumer);

        _logger.LogInformation(
            "PaymentService is consuming {Queue} with routing key {RoutingKey}",
            RabbitMqTopology.Queues.PaymentInventoryReserved,
            EventRoutingKeys.InventoryReserved);

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
            var envelope = JsonSerializer.Deserialize<EventEnvelope<InventoryReserved>>(json, JsonOptions)
                ?? throw new JsonException("InventoryReserved envelope could not be deserialized.");

            if (!string.Equals(envelope.EventType, nameof(InventoryReserved), StringComparison.Ordinal))
            {
                throw new JsonException($"Unexpected event type '{envelope.EventType}'.");
            }

            using var scope = _scopeFactory.CreateScope();
            var retryCount = RabbitMqRetryPolicy.GetRetryCountOrZero(args.BasicProperties.Headers);
            var processor = scope.ServiceProvider.GetRequiredService<PaymentProcessor>();
            await processor.HandleInventoryReservedAsync(envelope, CancellationToken.None, retryCount);

            _channel.BasicAck(args.DeliveryTag, multiple: false);
        }
        catch (JsonException ex)
        {
            RabbitMqMessageFailureHandler.DeadLetter(
                _channel,
                args,
                ex,
                "payment.failed",
                _logger,
                "Poison InventoryReserved message could not be deserialized.");
        }
        catch (Exception ex)
        {
            RabbitMqMessageFailureHandler.RetryOrDeadLetter(
                _channel,
                args,
                ex,
                "payment.failed",
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
