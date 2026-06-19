using System.Text;
using System.Text.Json;
using BuildingBlocks.EventBus.RabbitMQ;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace ReportingService.Messaging;

public sealed class RabbitMqReportingConsumer : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RabbitMqOptions _options;
    private readonly ILogger<RabbitMqReportingConsumer> _logger;
    private IConnection? _connection;
    private IModel? _channel;

    public RabbitMqReportingConsumer(
        IServiceScopeFactory scopeFactory,
        IOptions<RabbitMqOptions> options,
        ILogger<RabbitMqReportingConsumer> logger)
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

        _channel.QueueDeclare(RabbitMqTopology.Queues.ReportingAllEvents, durable: true, exclusive: false, autoDelete: false, arguments: arguments);
        _channel.QueueBind(RabbitMqTopology.Queues.ReportingAllEvents, _options.ExchangeName, "#");
        _channel.QueueDeclare(RabbitMqTopology.DeadLetterQueues.ReportingFailed, durable: true, exclusive: false, autoDelete: false);
        _channel.QueueBind(RabbitMqTopology.DeadLetterQueues.ReportingFailed, RabbitMqTopology.DeadLetterExchange, "reporting.#");
        _channel.BasicQos(prefetchSize: 0, prefetchCount: 25, global: false);

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.Received += HandleMessageAsync;
        _channel.BasicConsume(RabbitMqTopology.Queues.ReportingAllEvents, autoAck: false, consumer);

        _logger.LogInformation("ReportingService is consuming all platform events from {Queue}.", RabbitMqTopology.Queues.ReportingAllEvents);

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
            var source = root.TryGetProperty("source", out var sourceElement) ? sourceElement.GetString() ?? string.Empty : string.Empty;
            var occurredOnUtc = root.GetProperty("occurredOnUtc").GetDateTime();
            var payload = root.GetProperty("payload");
            var payloadJson = payload.GetRawText();

            using var scope = _scopeFactory.CreateScope();
            var handler = scope.ServiceProvider.GetRequiredService<ReportingProjectionHandler>();
            await handler.HandleAsync(messageId, correlationId, eventType, source, occurredOnUtc, payloadJson, payload, CancellationToken.None);
            _channel.BasicAck(args.DeliveryTag, multiple: false);
        }
        catch (JsonException ex)
        {
            RabbitMqMessageFailureHandler.DeadLetter(_channel, args, ex, "reporting.failed", _logger, "Poison reporting event could not be deserialized.");
        }
        catch (Exception ex)
        {
            RabbitMqMessageFailureHandler.RetryOrDeadLetter(_channel, args, ex, "reporting.failed", _logger);
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
