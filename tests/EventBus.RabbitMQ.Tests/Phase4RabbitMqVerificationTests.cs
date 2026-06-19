using System.Text;
using System.Text.Json;
using BuildingBlocks.Contracts;
using BuildingBlocks.Contracts.Events;
using BuildingBlocks.EventBus.RabbitMQ;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Testcontainers.RabbitMq;
using TestKit;
using Xunit;

namespace EventBusRabbitMQTests;

[Trait("Category", "Docker")]
public sealed class Phase4RabbitMqVerificationTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly RabbitMqContainer _rabbitMq = new RabbitMqBuilder()
        .WithImage("rabbitmq:3.13-management")
        .WithUsername("guest")
        .WithPassword("guest")
        .Build();

    public Task InitializeAsync() => _rabbitMq.StartAsync();

    public async Task DisposeAsync() => await _rabbitMq.DisposeAsync();

    [Fact]
    public async Task Poison_json_should_be_dead_lettered_with_diagnostic_metadata()
    {
        using var connection = RabbitMqConnectionFactory.Create(CreateOptions());
        using var channel = connection.CreateModel();
        RabbitMqMessageFailureHandler.DeclareReliabilityTopology(channel);

        var inputQueue = $"phase4.payment-events.{Guid.NewGuid():N}";
        channel.QueueDeclare(inputQueue, durable: false, exclusive: false, autoDelete: true, arguments: null);
        channel.QueueBind(inputQueue, RabbitMqTopology.Exchange, EventRoutingKeys.PaymentCompleted);
        channel.QueueDeclare(RabbitMqTopology.DeadLetterQueues.PaymentFailed, durable: true, exclusive: false, autoDelete: false, arguments: null);
        channel.QueueBind(RabbitMqTopology.DeadLetterQueues.PaymentFailed, RabbitMqTopology.DeadLetterExchange, "payment.#");

        var poisonHandled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.Received += (_, args) =>
        {
            try
            {
                var json = Encoding.UTF8.GetString(args.Body.ToArray());
                _ = JsonSerializer.Deserialize<EventEnvelope<PaymentCompleted>>(json, JsonOptions)
                    ?? throw new JsonException("PaymentCompleted envelope could not be deserialized.");
            }
            catch (JsonException ex)
            {
                RabbitMqMessageFailureHandler.DeadLetter(
                    channel,
                    args,
                    ex,
                    "payment.failed",
                    NullLogger.Instance,
                    "Poison PaymentCompleted message could not be deserialized.");
                poisonHandled.TrySetResult();
            }

            return Task.CompletedTask;
        };

        channel.BasicConsume(inputQueue, autoAck: false, consumer);

        var properties = channel.CreateBasicProperties();
        properties.Persistent = true;
        properties.ContentType = "application/json";
        properties.MessageId = "poison-payment-001";
        properties.CorrelationId = "poison-correlation-001";
        properties.Headers = new Dictionary<string, object>();

        channel.BasicPublish(
            RabbitMqTopology.Exchange,
            EventRoutingKeys.PaymentCompleted,
            mandatory: true,
            basicProperties: properties,
            body: Encoding.UTF8.GetBytes("{ invalid json"));

        await poisonHandled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Eventually.ShouldPass(() =>
        {
            channel.MessageCount(RabbitMqTopology.DeadLetterQueues.PaymentFailed).Should().Be(1u);
            return Task.CompletedTask;
        });

        var deadLetter = channel.BasicGet(RabbitMqTopology.DeadLetterQueues.PaymentFailed, autoAck: true);
        deadLetter.Should().NotBeNull();
        deadLetter!.Exchange.Should().Be(RabbitMqTopology.DeadLetterExchange);
        deadLetter.RoutingKey.Should().Be("payment.failed");
        Encoding.UTF8.GetString(deadLetter.Body.ToArray()).Should().Be("{ invalid json");
        deadLetter.BasicProperties.MessageId.Should().Be("poison-payment-001");
        deadLetter.BasicProperties.CorrelationId.Should().Be("poison-correlation-001");
        var headers = deadLetter.BasicProperties.Headers;
        headers.Should().NotBeNull();
        headers!.Should().ContainKey(RabbitMqRetryPolicy.ErrorTypeHeader);
        headers.Should().ContainKey(RabbitMqRetryPolicy.ErrorMessageHeader);
        headers.Should().ContainKey(RabbitMqRetryPolicy.FailedAtUtcHeader);
        HeaderAsString(headers, RabbitMqRetryPolicy.OriginalRoutingKeyHeader)
            .Should().Be(EventRoutingKeys.PaymentCompleted);
        HeaderAsString(headers, RabbitMqRetryPolicy.OriginalExchangeHeader)
            .Should().Be(RabbitMqTopology.Exchange);
        RabbitMqRetryPolicy.GetRetryCountOrZero(headers).Should().Be(0);
    }


    [Fact]
    public async Task Transient_failure_should_be_republished_to_first_retry_queue_with_retry_headers()
    {
        using var connection = RabbitMqConnectionFactory.Create(CreateOptions());
        using var channel = connection.CreateModel();
        RabbitMqMessageFailureHandler.DeclareReliabilityTopology(channel);

        var inputQueue = $"phase4.inventory-reserved.{Guid.NewGuid():N}";
        channel.QueueDeclare(inputQueue, durable: false, exclusive: false, autoDelete: true, arguments: null);
        channel.QueueBind(inputQueue, RabbitMqTopology.Exchange, EventRoutingKeys.InventoryReserved);
        channel.QueueDeclare(RabbitMqTopology.DeadLetterQueues.PaymentFailed, durable: true, exclusive: false, autoDelete: false, arguments: null);
        channel.QueueBind(RabbitMqTopology.DeadLetterQueues.PaymentFailed, RabbitMqTopology.DeadLetterExchange, "payment.#");

        var retryPublished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.Received += (_, args) =>
        {
            RabbitMqMessageFailureHandler.RetryOrDeadLetter(
                channel,
                args,
                new InvalidOperationException("simulated transient provider failure"),
                "payment.failed",
                NullLogger.Instance);
            retryPublished.TrySetResult();
            return Task.CompletedTask;
        };

        channel.BasicConsume(inputQueue, autoAck: false, consumer);

        var properties = channel.CreateBasicProperties();
        properties.Persistent = true;
        properties.ContentType = "application/json";
        properties.MessageId = "retry-payment-001";
        properties.CorrelationId = "retry-correlation-001";
        properties.Headers = new Dictionary<string, object>();

        channel.BasicPublish(
            RabbitMqTopology.Exchange,
            EventRoutingKeys.InventoryReserved,
            mandatory: true,
            basicProperties: properties,
            body: Encoding.UTF8.GetBytes("{}"));

        await retryPublished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Eventually.ShouldPass(() =>
        {
            channel.MessageCount(RabbitMqTopology.RetryQueues.Retry5Seconds).Should().Be(1u);
            return Task.CompletedTask;
        });

        var retryMessage = channel.BasicGet(RabbitMqTopology.RetryQueues.Retry5Seconds, autoAck: true);
        retryMessage.Should().NotBeNull();
        retryMessage!.Exchange.Should().Be(RabbitMqTopology.RetryExchanges.Retry5Seconds);
        retryMessage.RoutingKey.Should().Be(EventRoutingKeys.InventoryReserved);
        retryMessage.BasicProperties.MessageId.Should().Be("retry-payment-001");
        retryMessage.BasicProperties.CorrelationId.Should().Be("retry-correlation-001");

        var headers = retryMessage.BasicProperties.Headers;
        headers.Should().NotBeNull();
        headers!.Should().ContainKey(RabbitMqRetryPolicy.ErrorTypeHeader);
        headers.Should().ContainKey(RabbitMqRetryPolicy.ErrorMessageHeader);
        HeaderAsString(headers, RabbitMqRetryPolicy.OriginalRoutingKeyHeader)
            .Should().Be(EventRoutingKeys.InventoryReserved);
        HeaderAsString(headers, RabbitMqRetryPolicy.OriginalExchangeHeader)
            .Should().Be(RabbitMqTopology.Exchange);
        RabbitMqRetryPolicy.GetRetryCountOrZero(headers).Should().Be(1);
    }

    [Fact]
    public async Task Mandatory_publish_to_unbound_supported_routing_key_should_throw_unroutable_exception()
    {
        using var eventBus = new RabbitMqEventBus(
            Options.Create(CreateOptions()),
            NullLogger<RabbitMqEventBus>.Instance);

        var envelope = EventEnvelope.Create(
            new OrderCompleted(Guid.NewGuid(), Guid.NewGuid(), 24.68m, "USD"),
            Guid.NewGuid(),
            null,
            "OrderService",
            DateTime.UtcNow);

        var act = () => eventBus.PublishAsync(envelope, EventRoutingKeys.OrderCompleted, CancellationToken.None);

        await act.Should().ThrowAsync<RabbitMqUnroutableMessageException>()
            .Where(exception => exception.RoutingKey == EventRoutingKeys.OrderCompleted);
    }

    private RabbitMqOptions CreateOptions() =>
        new()
        {
            HostName = _rabbitMq.Hostname,
            Port = _rabbitMq.GetMappedPublicPort(5672),
            UserName = "guest",
            Password = "guest",
            VirtualHost = "/",
            ExchangeName = RabbitMqTopology.Exchange,
            PublisherConfirmTimeoutSeconds = 5
        };

    private static string HeaderAsString(IDictionary<string, object> headers, string key)
    {
        headers.Should().ContainKey(key);
        return headers[key] switch
        {
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            string text => text,
            _ => headers[key].ToString() ?? string.Empty
        };
    }
}
