using System.Text.Json;
using BuildingBlocks.Contracts;
using BuildingBlocks.Contracts.Events;
using BuildingBlocks.Persistence;
using BuildingBlocks.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaymentService.Diagnostics;
using PaymentService.Domain;
using PaymentService.Persistence;

namespace PaymentService.Messaging;

public sealed class PaymentProcessor
{
    public const string ConsumerName = "PaymentService.InventoryReserved";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly PaymentDbContext _dbContext;
    private readonly ISystemClock _clock;
    private readonly PaymentOptions _options;
    private readonly ILogger<PaymentProcessor> _logger;

    public PaymentProcessor(
        PaymentDbContext dbContext,
        ISystemClock clock,
        IOptions<PaymentOptions> options,
        ILogger<PaymentProcessor> logger)
    {
        _dbContext = dbContext;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<PaymentProcessingResult> HandleInventoryReservedAsync(
        EventEnvelope<InventoryReserved> envelope,
        CancellationToken cancellationToken,
        int currentRetryCount = 0)
    {
        var alreadyProcessed = await _dbContext.ProcessedMessages.AnyAsync(
            message => message.MessageId == envelope.MessageId && message.ConsumerName == ConsumerName,
            cancellationToken);

        if (alreadyProcessed)
        {
            _logger.LogInformation(
                "Skipping duplicate InventoryReserved message {MessageId} for order {OrderId}",
                envelope.MessageId,
                envelope.Payload.OrderId);

            return PaymentProcessingResult.Duplicate(envelope.Payload.OrderId);
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        await PaymentOrderLock.AcquireAsync(_dbContext, envelope.Payload.OrderId, cancellationToken);

        if (await IsProcessedAsync(envelope.MessageId, cancellationToken))
        {
            await transaction.CommitAsync(cancellationToken);
            return PaymentProcessingResult.Duplicate(envelope.Payload.OrderId);
        }

        var existingPayment = await _dbContext.PaymentAttempts.SingleOrDefaultAsync(
            payment => payment.OrderId == envelope.Payload.OrderId,
            cancellationToken);

        if (existingPayment is not null)
        {
            AddPaymentDuplicateRejectedOutboxMessage(envelope, existingPayment);
            AddProcessedMessage(envelope);
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            _logger.LogWarning(
                "Rejected duplicate payment request {MessageId} for order {OrderId}. Existing payment {PaymentId} has status {PaymentStatus}; no second charge was attempted.",
                envelope.MessageId,
                envelope.Payload.OrderId,
                existingPayment.Id,
                existingPayment.Status);

            return PaymentProcessingResult.DuplicateRejected(envelope.Payload.OrderId, existingPayment.Id, existingPayment.Status);
        }

        var cancellation = await _dbContext.CancelledOrders.AsNoTracking().SingleOrDefaultAsync(
            cancelledOrder => cancelledOrder.OrderId == envelope.Payload.OrderId,
            cancellationToken);

        if (cancellation is not null)
        {
            AddProcessedMessage(envelope);
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            _logger.LogInformation(
                "Skipping payment for cancelled order {OrderId}. InventoryReserved message {MessageId} was marked processed without charging.",
                envelope.Payload.OrderId,
                envelope.MessageId);

            return PaymentProcessingResult.Cancelled(envelope.Payload.OrderId);
        }

        var payment = PaymentAttempt.Create(
            envelope.Payload.OrderId,
            envelope.Payload.TotalAmount,
            envelope.Payload.Currency,
            _clock.UtcNow);

        var decision = await AuthorizePaymentAsync(envelope, payment, currentRetryCount, cancellationToken);

        var cancellationAfterAuthorization = await _dbContext.CancelledOrders.AsNoTracking().SingleOrDefaultAsync(
            cancelledOrder => cancelledOrder.OrderId == envelope.Payload.OrderId,
            cancellationToken);

        if (cancellationAfterAuthorization is not null)
        {
            AddProcessedMessage(envelope);
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            _logger.LogWarning(
                "Skipping payment for order {OrderId} because cancellation {CancellationReason} was recorded while authorization was in progress.",
                envelope.Payload.OrderId,
                cancellationAfterAuthorization.Reason);

            return PaymentProcessingResult.Cancelled(envelope.Payload.OrderId);
        }

        _dbContext.PaymentAttempts.Add(payment);

        if (decision.IsSuccess)
        {
            payment.Complete(decision.ProviderTransactionId!, _clock.UtcNow);
            AddPaymentCompletedOutboxMessage(envelope, payment);
            PaymentServiceDiagnostics.PaymentsCompleted.Add(1);
        }
        else
        {
            payment.Fail(decision.FailureCode!, decision.FailureReason!, _clock.UtcNow);
            AddPaymentFailedOutboxMessage(envelope, payment);
            PaymentServiceDiagnostics.PaymentsFailed.Add(1);
        }

        AddProcessedMessage(envelope);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        _logger.LogInformation(
            "Processed payment {PaymentId} for order {OrderId}; status {PaymentStatus}",
            payment.Id,
            payment.OrderId,
            payment.Status);

        return payment.Status == PaymentStatus.Completed
            ? PaymentProcessingResult.Completed(payment.OrderId, payment.Id)
            : PaymentProcessingResult.Failed(payment.OrderId, payment.Id, payment.FailureCode ?? "unknown");
    }

    private async Task<PaymentProviderDecision> AuthorizePaymentAsync(
        EventEnvelope<InventoryReserved> envelope,
        PaymentAttempt payment,
        int currentRetryCount,
        CancellationToken cancellationToken)
    {
        if (payment.Amount <= 0)
        {
            return PaymentProviderDecision.Failed("amount_invalid", "Payment amount must be greater than zero.");
        }

        if (!Money.SupportedCurrencies.Contains(payment.Currency))
        {
            return PaymentProviderDecision.Failed("currency_unsupported", $"Currency '{payment.Currency}' is unsupported.");
        }

        return _options.SimulationMode switch
        {
            PaymentSimulationMode.AlwaysSuccess => PaymentProviderDecision.Success(CreateProviderTransactionId(payment.OrderId)),
            PaymentSimulationMode.AlwaysFail => PaymentProviderDecision.Failed("provider_declined", "The simulated provider declined the payment."),
            PaymentSimulationMode.FailFirstThenSucceed => FailFirstThenSucceed(payment.OrderId, currentRetryCount),
            PaymentSimulationMode.Timeout => await TimeoutAsync(cancellationToken),
            PaymentSimulationMode.RandomFailure => Random.Shared.Next(0, 100) < _options.RandomFailurePercentage
                ? throw new PaymentTransientException("provider_random_failure", "The simulated provider randomly failed before authorization completed.")
                : PaymentProviderDecision.Success(CreateProviderTransactionId(payment.OrderId)),
            _ => PaymentProviderDecision.Success(CreateProviderTransactionId(payment.OrderId))
        };
    }

    private static PaymentProviderDecision FailFirstThenSucceed(Guid orderId, int currentRetryCount)
    {
        if (currentRetryCount == 0)
        {
            throw new PaymentTransientException(
                "provider_transient_first_attempt",
                "The deterministic provider failed transiently on the first attempt.");
        }

        return PaymentProviderDecision.Success(CreateProviderTransactionId(orderId));
    }

    private async Task<PaymentProviderDecision> TimeoutAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, _options.TimeoutMilliseconds)), cancellationToken);
        throw new PaymentTransientException("provider_timeout", "The simulated provider timed out before authorization completed.");
    }

    private Task<bool> IsProcessedAsync(Guid messageId, CancellationToken cancellationToken) =>
        _dbContext.ProcessedMessages.AnyAsync(
            message => message.MessageId == messageId && message.ConsumerName == ConsumerName,
            cancellationToken);

    private void AddProcessedMessage(EventEnvelope<InventoryReserved> envelope)
    {
        _dbContext.ProcessedMessages.Add(new ProcessedMessage
        {
            MessageId = envelope.MessageId,
            EventType = envelope.EventType,
            ConsumerName = ConsumerName,
            ProcessedAtUtc = _clock.UtcNow
        });
    }

    private void AddPaymentCompletedOutboxMessage(EventEnvelope<InventoryReserved> sourceEnvelope, PaymentAttempt payment)
    {
        var payload = new PaymentCompleted(
            payment.OrderId,
            payment.Id,
            payment.ProviderTransactionId ?? CreateProviderTransactionId(payment.OrderId),
            payment.Amount,
            payment.Currency);

        AddOutboxMessage(sourceEnvelope, payload, EventRoutingKeys.PaymentCompleted);
    }

    private void AddPaymentFailedOutboxMessage(EventEnvelope<InventoryReserved> sourceEnvelope, PaymentAttempt payment)
    {
        var payload = new PaymentFailed(
            payment.OrderId,
            payment.Id,
            payment.FailureCode ?? "provider_failed",
            payment.FailureReason ?? "Payment failed.",
            payment.Amount,
            payment.Currency);

        AddOutboxMessage(sourceEnvelope, payload, EventRoutingKeys.PaymentFailed);
    }


    private void AddPaymentDuplicateRejectedOutboxMessage(EventEnvelope<InventoryReserved> sourceEnvelope, PaymentAttempt existingPayment)
    {
        var reason = existingPayment.Status == PaymentStatus.Completed
            ? "order_already_paid"
            : $"existing_payment_status_{existingPayment.Status.ToString().ToLowerInvariant()}";

        var payload = new PaymentDuplicateRejected(
            existingPayment.OrderId,
            existingPayment.Id,
            existingPayment.ProviderTransactionId,
            existingPayment.Status.ToString(),
            existingPayment.Amount,
            existingPayment.Currency,
            reason);

        AddOutboxMessage(sourceEnvelope, payload, EventRoutingKeys.PaymentDuplicateRejected);
    }

    private void AddOutboxMessage<TPayload>(EventEnvelope<InventoryReserved> sourceEnvelope, TPayload payload, string routingKey)
    {
        var envelope = EventEnvelope.Create(
            payload,
            sourceEnvelope.CorrelationId,
            sourceEnvelope.MessageId,
            "PaymentService",
            _clock.UtcNow);

        _dbContext.OutboxMessages.Add(OutboxMessage.Create(
            envelope.MessageId,
            envelope.CorrelationId,
            envelope.EventType,
            envelope.EventVersion,
            routingKey,
            JsonSerializer.Serialize(envelope, JsonOptions),
            envelope.OccurredOnUtc));
    }

    private static string CreateProviderTransactionId(Guid orderId) => $"sim-{orderId:N}";
}

public sealed record PaymentProviderDecision(bool IsSuccess, string? ProviderTransactionId, string? FailureCode, string? FailureReason)
{
    public static PaymentProviderDecision Success(string providerTransactionId) => new(true, providerTransactionId, null, null);
    public static PaymentProviderDecision Failed(string failureCode, string failureReason) => new(false, null, failureCode, failureReason);
}

public sealed class PaymentTransientException : Exception
{
    public PaymentTransientException(string failureCode, string message) : base(message)
    {
        FailureCode = failureCode;
    }

    public string FailureCode { get; }
}

public enum PaymentProcessingOutcome
{
    Completed = 0,
    Failed = 1,
    Duplicate = 2,
    AlreadyProcessed = 3,
    Cancelled = 4,
    DuplicateRejected = 5
}

public sealed record PaymentProcessingResult(Guid OrderId, Guid? PaymentId, PaymentProcessingOutcome Outcome, string? FailureCode)
{
    public static PaymentProcessingResult Completed(Guid orderId, Guid paymentId) => new(orderId, paymentId, PaymentProcessingOutcome.Completed, null);
    public static PaymentProcessingResult Failed(Guid orderId, Guid paymentId, string failureCode) => new(orderId, paymentId, PaymentProcessingOutcome.Failed, failureCode);
    public static PaymentProcessingResult Duplicate(Guid orderId) => new(orderId, null, PaymentProcessingOutcome.Duplicate, null);
    public static PaymentProcessingResult AlreadyProcessed(Guid orderId, Guid paymentId, PaymentStatus status) => new(orderId, paymentId, PaymentProcessingOutcome.AlreadyProcessed, status.ToString());
    public static PaymentProcessingResult DuplicateRejected(Guid orderId, Guid paymentId, PaymentStatus status) => new(orderId, paymentId, PaymentProcessingOutcome.DuplicateRejected, status.ToString());
    public static PaymentProcessingResult Cancelled(Guid orderId) => new(orderId, null, PaymentProcessingOutcome.Cancelled, "order_cancelled");
}
