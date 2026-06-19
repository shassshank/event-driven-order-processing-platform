namespace PaymentService.Outbox;

public sealed class OutboxOptions
{
    public bool Enabled { get; init; } = true;
    public int PollIntervalMilliseconds { get; init; } = 1000;
    public int BatchSize { get; init; } = 20;
    public int MaxPublishAttempts { get; init; } = 10;
    public int ProcessingTimeoutSeconds { get; init; } = 120;
}
