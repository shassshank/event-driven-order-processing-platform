using RabbitMQ.Client.Events;

namespace BuildingBlocks.EventBus.RabbitMQ;

public sealed record RabbitMqRetryDecision(
    bool ShouldRetry,
    bool ShouldDeadLetter,
    int CurrentRetryCount,
    int NextRetryCount,
    string? RetryExchangeName,
    TimeSpan? Delay,
    string Reason);

public static class RabbitMqRetryPolicy
{
    public const string RetryCountHeader = "x-retry-count";
    public const string OriginalRoutingKeyHeader = "x-original-routing-key";
    public const string ErrorMessageHeader = "x-error-message";
    public const string ErrorTypeHeader = "x-error-type";
    public const string FailedAtUtcHeader = "x-failed-at-utc";
    public const string OriginalExchangeHeader = "x-original-exchange";
    public const int DefaultMaxRetries = 3;

    public static RabbitMqRetryDecision Decide(IDictionary<string, object>? headers, int maxRetries = DefaultMaxRetries)
    {
        var retryCount = ReadRetryCount(headers);
        if (retryCount is null)
        {
            return new RabbitMqRetryDecision(
                ShouldRetry: false,
                ShouldDeadLetter: true,
                CurrentRetryCount: 0,
                NextRetryCount: 0,
                RetryExchangeName: null,
                Delay: null,
                Reason: "Retry count header is corrupted.");
        }

        if (retryCount.Value >= maxRetries)
        {
            return new RabbitMqRetryDecision(
                ShouldRetry: false,
                ShouldDeadLetter: true,
                CurrentRetryCount: retryCount.Value,
                NextRetryCount: retryCount.Value,
                RetryExchangeName: null,
                Delay: null,
                Reason: "Maximum retry count exceeded.");
        }

        var nextRetry = retryCount.Value + 1;
        var (exchange, delay) = nextRetry switch
        {
            1 => (RabbitMqTopology.RetryExchanges.Retry5Seconds, TimeSpan.FromSeconds(5)),
            2 => (RabbitMqTopology.RetryExchanges.Retry30Seconds, TimeSpan.FromSeconds(30)),
            _ => (RabbitMqTopology.RetryExchanges.Retry2Minutes, TimeSpan.FromMinutes(2))
        };

        return new RabbitMqRetryDecision(
            ShouldRetry: true,
            ShouldDeadLetter: false,
            CurrentRetryCount: retryCount.Value,
            NextRetryCount: nextRetry,
            RetryExchangeName: exchange,
            Delay: delay,
            Reason: "Transient failure will be retried.");
    }

    public static string GetOriginalRoutingKey(BasicDeliverEventArgs args)
    {
        if (args.BasicProperties.Headers is not null &&
            args.BasicProperties.Headers.TryGetValue(OriginalRoutingKeyHeader, out var value) &&
            TryReadString(value, out var originalRoutingKey) &&
            !string.IsNullOrWhiteSpace(originalRoutingKey))
        {
            return originalRoutingKey;
        }

        return args.RoutingKey;
    }

    public static int GetRetryCountOrZero(IDictionary<string, object>? headers) =>
        ReadRetryCount(headers) ?? 0;

    private static int? ReadRetryCount(IDictionary<string, object>? headers)
    {
        if (headers is null || !headers.TryGetValue(RetryCountHeader, out var value) || value is null)
        {
            return 0;
        }

        return value switch
        {
            byte b => b,
            short s when s >= 0 => s,
            int i when i >= 0 => i,
            long l when l >= 0 && l <= int.MaxValue => (int)l,
            byte[] bytes when int.TryParse(System.Text.Encoding.UTF8.GetString(bytes), out var parsedBytes) && parsedBytes >= 0 => parsedBytes,
            string text when int.TryParse(text, out var parsedText) && parsedText >= 0 => parsedText,
            _ => null
        };
    }

    private static bool TryReadString(object value, out string text)
    {
        switch (value)
        {
            case string stringValue:
                text = stringValue;
                return true;
            case byte[] bytes:
                text = System.Text.Encoding.UTF8.GetString(bytes);
                return true;
            default:
                text = string.Empty;
                return false;
        }
    }
}
