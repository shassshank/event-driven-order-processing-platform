using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace BuildingBlocks.EventBus.RabbitMQ;

public static class RabbitMqMessageFailureHandler
{
    public static void RetryOrDeadLetter(
        IModel channel,
        BasicDeliverEventArgs args,
        Exception exception,
        string deadLetterRoutingKey,
        ILogger logger,
        int maxRetries = RabbitMqRetryPolicy.DefaultMaxRetries)
    {
        var decision = RabbitMqRetryPolicy.Decide(args.BasicProperties.Headers, maxRetries);
        var originalRoutingKey = RabbitMqRetryPolicy.GetOriginalRoutingKey(args);

        if (decision.ShouldRetry && decision.RetryExchangeName is not null)
        {
            var published = TryPublishCopyWithConfirm(
                channel,
                args,
                decision.RetryExchangeName,
                originalRoutingKey,
                exception,
                originalRoutingKey,
                decision.NextRetryCount,
                logger);

            if (!published)
            {
                NackOriginalForRedelivery(channel, args, logger, "retry publish was not confirmed");
                return;
            }

            channel.BasicAck(args.DeliveryTag, multiple: false);
            logger.LogWarning(
                exception,
                "Retried RabbitMQ message {MessageId}. Retry {RetryCount}/{MaxRetries}. OriginalRoutingKey={OriginalRoutingKey}. RetryExchange={RetryExchange}",
                args.BasicProperties.MessageId,
                decision.NextRetryCount,
                maxRetries,
                originalRoutingKey,
                decision.RetryExchangeName);
            return;
        }

        DeadLetter(channel, args, exception, deadLetterRoutingKey, logger, decision.Reason);
    }

    public static void DeadLetter(
        IModel channel,
        BasicDeliverEventArgs args,
        Exception exception,
        string deadLetterRoutingKey,
        ILogger logger,
        string reason = "Poison or non-retryable message.")
    {
        var originalRoutingKey = RabbitMqRetryPolicy.GetOriginalRoutingKey(args);
        var retryCount = RabbitMqRetryPolicy.Decide(args.BasicProperties.Headers).CurrentRetryCount;

        var published = TryPublishCopyWithConfirm(
            channel,
            args,
            RabbitMqTopology.DeadLetterExchange,
            deadLetterRoutingKey,
            exception,
            originalRoutingKey,
            retryCount,
            logger);

        if (!published)
        {
            NackOriginalForRedelivery(channel, args, logger, "dead-letter publish was not confirmed");
            return;
        }

        channel.BasicAck(args.DeliveryTag, multiple: false);
        logger.LogError(
            exception,
            "Dead-lettered RabbitMQ message {MessageId}. Reason={Reason}. OriginalRoutingKey={OriginalRoutingKey}. DeadLetterRoutingKey={DeadLetterRoutingKey}",
            args.BasicProperties.MessageId,
            reason,
            originalRoutingKey,
            deadLetterRoutingKey);
    }

    private static bool TryPublishCopyWithConfirm(
        IModel channel,
        BasicDeliverEventArgs args,
        string exchange,
        string routingKey,
        Exception exception,
        string originalRoutingKey,
        int retryCount,
        ILogger logger)
    {
        lock (channel)
        {
            var returned = false;
            string? returnReplyText = null;
            ushort returnReplyCode = 0;

            void OnBasicReturn(object? _, BasicReturnEventArgs returnedArgs)
            {
                returned = true;
                returnReplyText = returnedArgs.ReplyText;
                returnReplyCode = returnedArgs.ReplyCode;
            }

            try
            {
                DeclareReliabilityTopology(channel);
                channel.ConfirmSelect();
                channel.BasicReturn += OnBasicReturn;

                var properties = channel.CreateBasicProperties();
                properties.Persistent = true;
                properties.ContentType = args.BasicProperties.ContentType ?? "application/json";
                properties.MessageId = args.BasicProperties.MessageId;
                properties.CorrelationId = args.BasicProperties.CorrelationId;
                properties.Type = args.BasicProperties.Type;
                properties.Timestamp = args.BasicProperties.Timestamp.UnixTime > 0
                    ? args.BasicProperties.Timestamp
                    : new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                properties.Headers = CopyHeaders(args.BasicProperties.Headers);
                properties.Headers[RabbitMqRetryPolicy.RetryCountHeader] = retryCount;
                properties.Headers[RabbitMqRetryPolicy.OriginalRoutingKeyHeader] = originalRoutingKey;
                properties.Headers[RabbitMqRetryPolicy.OriginalExchangeHeader] = args.Exchange;
                properties.Headers[RabbitMqRetryPolicy.ErrorTypeHeader] = exception.GetType().Name;
                properties.Headers[RabbitMqRetryPolicy.ErrorMessageHeader] = Truncate(exception.Message, 1000);
                properties.Headers[RabbitMqRetryPolicy.FailedAtUtcHeader] = DateTimeOffset.UtcNow.ToString("O");

                channel.BasicPublish(
                    exchange: exchange,
                    routingKey: routingKey,
                    mandatory: true,
                    basicProperties: properties,
                    body: args.Body);

                var confirmed = channel.WaitForConfirms(TimeSpan.FromSeconds(10));
                if (!confirmed)
                {
                    logger.LogError(
                        "RabbitMQ did not confirm retry/DLQ publish for original message {MessageId} to {Exchange}/{RoutingKey}.",
                        args.BasicProperties.MessageId,
                        exchange,
                        routingKey);
                    return false;
                }

                if (returned)
                {
                    logger.LogError(
                        "RabbitMQ returned retry/DLQ publish for original message {MessageId} to {Exchange}/{RoutingKey}. ReplyCode={ReplyCode}. ReplyText={ReplyText}.",
                        args.BasicProperties.MessageId,
                        exchange,
                        routingKey,
                        returnReplyCode,
                        returnReplyText);
                    return false;
                }

                return true;
            }
            catch (Exception publishException)
            {
                logger.LogError(
                    publishException,
                    "Could not republish failed RabbitMQ message {MessageId} to {Exchange}/{RoutingKey}. The original message will be redelivered instead of acknowledged.",
                    args.BasicProperties.MessageId,
                    exchange,
                    routingKey);
                return false;
            }
            finally
            {
                channel.BasicReturn -= OnBasicReturn;
            }
        }
    }

    public static void DeclareReliabilityTopology(IModel channel)
    {
        channel.ExchangeDeclare(RabbitMqTopology.Exchange, ExchangeType.Topic, durable: true, autoDelete: false);
        channel.ExchangeDeclare(RabbitMqTopology.DeadLetterExchange, ExchangeType.Topic, durable: true, autoDelete: false);

        channel.ExchangeDeclare(RabbitMqTopology.RetryExchanges.Retry5Seconds, ExchangeType.Topic, durable: true, autoDelete: false);
        channel.ExchangeDeclare(RabbitMqTopology.RetryExchanges.Retry30Seconds, ExchangeType.Topic, durable: true, autoDelete: false);
        channel.ExchangeDeclare(RabbitMqTopology.RetryExchanges.Retry2Minutes, ExchangeType.Topic, durable: true, autoDelete: false);

        DeclareRetryQueue(channel, RabbitMqTopology.RetryQueues.Retry5Seconds, 5_000);
        DeclareRetryQueue(channel, RabbitMqTopology.RetryQueues.Retry30Seconds, 30_000);
        DeclareRetryQueue(channel, RabbitMqTopology.RetryQueues.Retry2Minutes, 120_000);

        channel.QueueBind(RabbitMqTopology.RetryQueues.Retry5Seconds, RabbitMqTopology.RetryExchanges.Retry5Seconds, "#");
        channel.QueueBind(RabbitMqTopology.RetryQueues.Retry30Seconds, RabbitMqTopology.RetryExchanges.Retry30Seconds, "#");
        channel.QueueBind(RabbitMqTopology.RetryQueues.Retry2Minutes, RabbitMqTopology.RetryExchanges.Retry2Minutes, "#");
    }

    private static void NackOriginalForRedelivery(IModel channel, BasicDeliverEventArgs args, ILogger logger, string reason)
    {
        try
        {
            channel.BasicNack(args.DeliveryTag, multiple: false, requeue: true);
            logger.LogWarning(
                "Nacked RabbitMQ message {MessageId} for redelivery because {Reason}.",
                args.BasicProperties.MessageId,
                reason);
        }
        catch (Exception nackException)
        {
            logger.LogError(
                nackException,
                "Could not nack RabbitMQ message {MessageId} after {Reason}. The broker should redeliver when the channel closes.",
                args.BasicProperties.MessageId,
                reason);
        }
    }

    private static void DeclareRetryQueue(IModel channel, string queueName, int ttlMilliseconds)
    {
        var arguments = new Dictionary<string, object>
        {
            ["x-message-ttl"] = ttlMilliseconds,
            ["x-dead-letter-exchange"] = RabbitMqTopology.Exchange
        };

        channel.QueueDeclare(
            queueName,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: arguments);
    }

    private static Dictionary<string, object> CopyHeaders(IDictionary<string, object>? headers)
    {
        return headers is null
            ? new Dictionary<string, object>()
            : new Dictionary<string, object>(headers);
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength];
    }
}
