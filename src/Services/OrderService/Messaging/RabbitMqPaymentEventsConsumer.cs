using System.Text;
using System.Text.Json;
using BuildingBlocks.Contracts;
using BuildingBlocks.Contracts.Events;
using BuildingBlocks.EventBus.RabbitMQ;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace OrderService.Messaging;

public sealed class RabbitMqPaymentEventsConsumer : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RabbitMqOptions _options;
    private readonly ILogger<RabbitMqPaymentEventsConsumer> _logger;
    private IConnection? _connection;
    private IModel? _channel;

    public RabbitMqPaymentEventsConsumer(
        IServiceScopeFactory scopeFactory,
        IOptions<RabbitMqOptions> options,
        ILogger<RabbitMqPaymentEventsConsumer> logger)
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
            RabbitMqTopology.Queues.OrderPaymentEvents,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: arguments);
        _channel.QueueBind(RabbitMqTopology.Queues.OrderPaymentEvents, _options.ExchangeName, EventRoutingKeys.PaymentCompleted);
        _channel.QueueBind(RabbitMqTopology.Queues.OrderPaymentEvents, _options.ExchangeName, EventRoutingKeys.PaymentFailed);

        _channel.BasicQos(prefetchSize: 0, prefetchCount: 10, global: false);

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.Received += HandleMessageAsync;

        _channel.BasicConsume(
            queue: RabbitMqTopology.Queues.OrderPaymentEvents,
            autoAck: false,
            consumer: consumer);

        _logger.LogInformation("OrderService is consuming payment events from queue {Queue}", RabbitMqTopology.Queues.OrderPaymentEvents);

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
                throw new JsonException("Payment event envelope does not contain eventType.");
            }

            var eventType = eventTypeElement.GetString();
            using var scope = _scopeFactory.CreateScope();
            var handler = scope.ServiceProvider.GetRequiredService<PaymentEventHandler>();

            switch (eventType)
            {
                case nameof(PaymentCompleted):
                {
                    var envelope = JsonSerializer.Deserialize<EventEnvelope<PaymentCompleted>>(json, JsonOptions)
                        ?? throw new JsonException("PaymentCompleted envelope could not be deserialized.");
                    await handler.HandlePaymentCompletedAsync(envelope, CancellationToken.None);
                    break;
                }
                case nameof(PaymentFailed):
                {
                    var envelope = JsonSerializer.Deserialize<EventEnvelope<PaymentFailed>>(json, JsonOptions)
                        ?? throw new JsonException("PaymentFailed envelope could not be deserialized.");
                    await handler.HandlePaymentFailedAsync(envelope, CancellationToken.None);
                    break;
                }
                default:
                    throw new JsonException($"Unexpected payment event type '{eventType}'.");
            }

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
                "Poison payment event could not be deserialized.");
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
