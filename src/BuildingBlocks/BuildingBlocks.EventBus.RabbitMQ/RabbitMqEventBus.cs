using System.Text;
using System.Text.Json;
using BuildingBlocks.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace BuildingBlocks.EventBus.RabbitMQ;

public sealed class RabbitMqEventBus : IEventBus, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly RabbitMqOptions _options;
    private readonly ILogger<RabbitMqEventBus> _logger;
    private readonly object _publishLock = new();
    private IConnection? _connection;
    private bool _disposed;

    public RabbitMqEventBus(IOptions<RabbitMqOptions> options, ILogger<RabbitMqEventBus> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public Task PublishAsync<TPayload>(EventEnvelope<TPayload> envelope, string routingKey, CancellationToken cancellationToken = default)
    {
        var envelopeJson = JsonSerializer.Serialize(envelope, JsonOptions);
        return PublishRawAsync(
            envelopeJson,
            routingKey,
            envelope.MessageId,
            envelope.CorrelationId,
            envelope.EventType,
            envelope.EventVersion,
            envelope.OccurredOnUtc,
            cancellationToken,
            envelope.CausationId);
    }

    public Task PublishRawAsync(
        string envelopeJson,
        string routingKey,
        Guid messageId,
        Guid correlationId,
        string eventType,
        int eventVersion,
        DateTime occurredOnUtc,
        CancellationToken cancellationToken = default,
        Guid? causationId = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RoutingKeyValidator.ThrowIfUnsupported(routingKey);

        lock (_publishLock)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var channel = GetConnection().CreateModel();
            RabbitMqMessageFailureHandler.DeclareReliabilityTopology(channel);
            channel.ConfirmSelect();

            var returned = false;
            string? returnReplyText = null;
            ushort returnReplyCode = 0;
            void OnBasicReturn(object? _, BasicReturnEventArgs args)
            {
                returned = true;
                returnReplyText = args.ReplyText;
                returnReplyCode = args.ReplyCode;
            }

            channel.BasicReturn += OnBasicReturn;
            try
            {
                var properties = channel.CreateBasicProperties();
                properties.Persistent = true;
                properties.ContentType = "application/json";
                properties.MessageId = messageId.ToString("D");
                properties.CorrelationId = correlationId.ToString("D");
                properties.Type = eventType;
                properties.Timestamp = new AmqpTimestamp(new DateTimeOffset(occurredOnUtc).ToUnixTimeSeconds());
                properties.Headers = new Dictionary<string, object>
                {
                    ["event-type"] = eventType,
                    ["event-version"] = eventVersion,
                    ["correlation-id"] = correlationId.ToString("D"),
                    ["message-id"] = messageId.ToString("D"),
                    ["causation-id"] = (causationId ?? correlationId).ToString("D")
                };

                var body = Encoding.UTF8.GetBytes(envelopeJson);
                channel.BasicPublish(
                    exchange: _options.ExchangeName,
                    routingKey: routingKey,
                    mandatory: true,
                    basicProperties: properties,
                    body: body);

                var confirmed = channel.WaitForConfirms(TimeSpan.FromSeconds(_options.PublisherConfirmTimeoutSeconds));
                if (!confirmed)
                {
                    throw new TimeoutException($"RabbitMQ publisher confirm timed out for message '{messageId}' on routing key '{routingKey}'.");
                }

                if (returned)
                {
                    throw new RabbitMqUnroutableMessageException(
                        messageId.ToString("D"),
                        routingKey,
                        returnReplyCode,
                        returnReplyText ?? "RabbitMQ returned the mandatory publish as unroutable.");
                }

                _logger.LogInformation(
                    "Published RabbitMQ message {MessageId} with routing key {RoutingKey} and correlation {CorrelationId}",
                    messageId,
                    routingKey,
                    correlationId);
            }
            finally
            {
                channel.BasicReturn -= OnBasicReturn;
            }
        }

        return Task.CompletedTask;
    }

    private IConnection GetConnection()
    {
        if (_connection is { IsOpen: true })
        {
            return _connection;
        }

        _connection?.Dispose();
        _connection = RabbitMqConnectionFactory.Create(_options);
        return _connection;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _connection?.Dispose();
        _disposed = true;
    }
}

public sealed class RabbitMqUnroutableMessageException : InvalidOperationException
{
    public RabbitMqUnroutableMessageException(string messageId, string routingKey, ushort replyCode, string replyText)
        : base($"RabbitMQ returned mandatory message '{messageId}' as unroutable for routing key '{routingKey}'. ReplyCode={replyCode}. ReplyText={replyText}")
    {
        MessageId = messageId;
        RoutingKey = routingKey;
        ReplyCode = replyCode;
        ReplyText = replyText;
    }

    public string MessageId { get; }
    public string RoutingKey { get; }
    public ushort ReplyCode { get; }
    public string ReplyText { get; }
}
