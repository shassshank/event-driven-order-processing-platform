using System.Text.Json;
using BuildingBlocks.Contracts;
using BuildingBlocks.Contracts.Events;
using BuildingBlocks.Persistence;
using BuildingBlocks.SharedKernel;
using InventoryService.Domain;
using InventoryService.Persistence;
using Microsoft.EntityFrameworkCore;

namespace InventoryService.Messaging;

public sealed class InventoryReservationService
{
    public const string ConsumerName = "InventoryService.OrderCreated";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly InventoryDbContext _dbContext;
    private readonly ISystemClock _clock;
    private readonly ILogger<InventoryReservationService> _logger;

    public InventoryReservationService(
        InventoryDbContext dbContext,
        ISystemClock clock,
        ILogger<InventoryReservationService> logger)
    {
        _dbContext = dbContext;
        _clock = clock;
        _logger = logger;
    }

    public async Task<InventoryReservationResult> HandleOrderCreatedAsync(
        EventEnvelope<OrderCreated> envelope,
        CancellationToken cancellationToken)
    {
        var alreadyProcessed = await _dbContext.ProcessedMessages.AnyAsync(
            message => message.MessageId == envelope.MessageId && message.ConsumerName == ConsumerName,
            cancellationToken);

        if (alreadyProcessed)
        {
            _logger.LogInformation(
                "Skipping duplicate OrderCreated message {MessageId} for order {OrderId}",
                envelope.MessageId,
                envelope.Payload.OrderId);

            return InventoryReservationResult.Duplicate(envelope.Payload.OrderId);
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        var orderAlreadyCancelled = await _dbContext.CancelledOrders.AnyAsync(
            cancelled => cancelled.OrderId == envelope.Payload.OrderId,
            cancellationToken);

        if (orderAlreadyCancelled)
        {
            AddProcessedMessage(envelope);
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            _logger.LogInformation(
                "Skipping OrderCreated message {MessageId} for already-cancelled order {OrderId}.",
                envelope.MessageId,
                envelope.Payload.OrderId);

            return InventoryReservationResult.Cancelled(envelope.Payload.OrderId);
        }

        var existingReservations = await _dbContext.Reservations
            .Where(reservation => reservation.OrderId == envelope.Payload.OrderId)
            .ToListAsync(cancellationToken);

        if (existingReservations.Count > 0)
        {
            AddProcessedMessage(envelope);
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            _logger.LogInformation(
                "Skipping logical duplicate OrderCreated message {MessageId} for order {OrderId}; {ReservationCount} reservations already exist.",
                envelope.MessageId,
                envelope.Payload.OrderId,
                existingReservations.Count);

            return InventoryReservationResult.Duplicate(envelope.Payload.OrderId);
        }

        var failedItems = new List<InventoryFailureItem>();
        var productIds = envelope.Payload.Items.Select(item => item.ProductId).Distinct().ToArray();
        var productsById = await _dbContext.Products
            .Where(product => productIds.Contains(product.Id))
            .ToDictionaryAsync(product => product.Id, cancellationToken);

        foreach (var item in envelope.Payload.Items)
        {
            if (!productsById.TryGetValue(item.ProductId, out var product))
            {
                failedItems.Add(new InventoryFailureItem(item.ProductId, item.Quantity, 0, "Product does not exist."));
                continue;
            }

            if (!product.CanReserve(item.Quantity))
            {
                failedItems.Add(new InventoryFailureItem(item.ProductId, item.Quantity, product.AvailableStock, "Insufficient stock."));
            }
        }

        if (failedItems.Count > 0)
        {
            foreach (var item in envelope.Payload.Items)
            {
                _dbContext.Reservations.Add(InventoryReservation.Failed(envelope.Payload.OrderId, item.ProductId, item.Quantity, _clock.UtcNow));
            }

            AddProcessedMessage(envelope);
            AddInventoryReservationFailedOutboxMessage(envelope, failedItems);

            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            _logger.LogInformation(
                "Failed inventory reservation for order {OrderId} from message {MessageId}",
                envelope.Payload.OrderId,
                envelope.MessageId);

            return InventoryReservationResult.Failed(envelope.Payload.OrderId, failedItems);
        }

        foreach (var item in envelope.Payload.Items)
        {
            var product = productsById[item.ProductId];
            product.Reserve(item.Quantity, _clock.UtcNow);
            _dbContext.Reservations.Add(InventoryReservation.Reserved(envelope.Payload.OrderId, item.ProductId, item.Quantity, _clock.UtcNow));
        }

        AddProcessedMessage(envelope);
        AddInventoryReservedOutboxMessage(envelope);

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        _logger.LogInformation(
            "Reserved inventory for order {OrderId} from message {MessageId}",
            envelope.Payload.OrderId,
            envelope.MessageId);

        return InventoryReservationResult.Reserved(envelope.Payload.OrderId);
    }

    private void AddProcessedMessage(EventEnvelope<OrderCreated> envelope)
    {
        _dbContext.ProcessedMessages.Add(new ProcessedMessage
        {
            MessageId = envelope.MessageId,
            EventType = envelope.EventType,
            ConsumerName = ConsumerName,
            ProcessedAtUtc = _clock.UtcNow
        });
    }

    private void AddInventoryReservedOutboxMessage(EventEnvelope<OrderCreated> envelope)
    {
        var payload = new InventoryReserved(
            envelope.Payload.OrderId,
            envelope.Payload.TotalAmount,
            envelope.Payload.Currency,
            envelope.Payload.Items.Select(item => new InventoryReservedItem(item.ProductId, item.Quantity)).ToArray());

        AddOutboxMessage(envelope, payload, EventRoutingKeys.InventoryReserved);
    }

    private void AddInventoryReservationFailedOutboxMessage(
        EventEnvelope<OrderCreated> envelope,
        IReadOnlyCollection<InventoryFailureItem> failedItems)
    {
        var payload = new InventoryReservationFailed(
            envelope.Payload.OrderId,
            "Inventory could not be reserved for one or more order items.",
            failedItems.ToArray());

        AddOutboxMessage(envelope, payload, EventRoutingKeys.InventoryReservationFailed);
    }

    private void AddOutboxMessage<TPayload>(EventEnvelope<OrderCreated> sourceEnvelope, TPayload payload, string routingKey)
    {
        var envelope = EventEnvelope.Create(
            payload,
            sourceEnvelope.CorrelationId,
            sourceEnvelope.MessageId,
            "InventoryService",
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
}

public enum InventoryReservationOutcome
{
    Reserved = 0,
    Failed = 1,
    Duplicate = 2,
    Cancelled = 3
}

public sealed record InventoryReservationResult(
    Guid OrderId,
    InventoryReservationOutcome Outcome,
    IReadOnlyCollection<InventoryFailureItem> FailedItems)
{
    public static InventoryReservationResult Reserved(Guid orderId) => new(orderId, InventoryReservationOutcome.Reserved, []);
    public static InventoryReservationResult Failed(Guid orderId, IReadOnlyCollection<InventoryFailureItem> failedItems) => new(orderId, InventoryReservationOutcome.Failed, failedItems);
    public static InventoryReservationResult Duplicate(Guid orderId) => new(orderId, InventoryReservationOutcome.Duplicate, []);
    public static InventoryReservationResult Cancelled(Guid orderId) => new(orderId, InventoryReservationOutcome.Cancelled, []);
}
