using System.Diagnostics;
using System.Text.Json;
using BuildingBlocks.Contracts;
using BuildingBlocks.Contracts.Events;
using BuildingBlocks.Observability;
using BuildingBlocks.Persistence;
using BuildingBlocks.SharedKernel;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderService.Caching;
using OrderService.Domain;
using OrderService.Persistence;

namespace OrderService.Features.Orders;

public sealed class OrderApplicationService : IOrderApplicationService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly OrderDbContext _dbContext;
    private readonly IValidator<CreateOrderRequest> _validator;
    private readonly ISystemClock _clock;
    private readonly ICorrelationContext _correlationContext;
    private readonly IOrderCache _orderCache;
    private readonly ILogger<OrderApplicationService> _logger;

    public OrderApplicationService(
        OrderDbContext dbContext,
        IValidator<CreateOrderRequest> validator,
        ISystemClock clock,
        ICorrelationContext correlationContext,
        IOrderCache orderCache,
        ILogger<OrderApplicationService> logger)
    {
        _dbContext = dbContext;
        _validator = validator;
        _clock = clock;
        _correlationContext = correlationContext;
        _orderCache = orderCache;
        _logger = logger;
    }

    public async Task<Result<OrderResponse>> CreateOrderAsync(CreateOrderRequest request, CancellationToken cancellationToken)
    {
        using var activity = OrderServiceDiagnostics.ActivitySource.StartActivity("orders.create", ActivityKind.Internal);

        var validationResult = await _validator.ValidateAsync(request, cancellationToken);
        if (!validationResult.IsValid)
        {
            var firstError = validationResult.Errors[0];
            return Result.Failure<OrderResponse>(new Error(firstError.ErrorCode, firstError.ErrorMessage));
        }

        if (await _dbContext.Orders.AnyAsync(order => order.ClientRequestId == request.ClientRequestId, cancellationToken))
        {
            return Result.Failure<OrderResponse>(OrderErrors.DuplicateClientRequestId(request.ClientRequestId));
        }

        var customerId = Guid.Parse(request.CustomerId);
        var orderItems = request.Items.Select(item => (Guid.Parse(item.ProductId), item.Quantity, item.UnitPrice));
        var order = OrderAggregate.Create(customerId, request.ClientRequestId, orderItems, request.Currency, _clock.UtcNow);

        var orderCreated = new OrderCreated(
            order.Id,
            order.CustomerId,
            order.ClientRequestId,
            order.Items.Select(item => new OrderCreatedItem(item.ProductId, item.Quantity, item.UnitPrice)).ToArray(),
            order.TotalAmount,
            order.Currency);

        var envelope = EventEnvelope.Create(
            orderCreated,
            _correlationContext.CorrelationId,
            causationId: null,
            source: OrderServiceDiagnostics.ServiceName,
            occurredOnUtc: _clock.UtcNow);

        var payload = JsonSerializer.Serialize(envelope, JsonOptions);
        var outboxMessage = OutboxMessage.Create(
            envelope.MessageId,
            envelope.CorrelationId,
            envelope.EventType,
            envelope.EventVersion,
            EventRoutingKeys.OrderCreated,
            payload,
            envelope.OccurredOnUtc);

        await PersistOrderWithOutboxMessageAsync(order, outboxMessage, cancellationToken);
        await _orderCache.RemoveAsync(order.Id, cancellationToken);

        OrderServiceDiagnostics.OrdersCreated.Add(1);
        activity?.SetTag("order.id", order.Id.ToString());
        activity?.SetTag("order.total", order.TotalAmount);

        _logger.LogInformation(
            "Created order {OrderId} with outbox message {MessageId} and correlation {CorrelationId}",
            order.Id,
            outboxMessage.MessageId,
            outboxMessage.CorrelationId);

        return Result.Success(ToResponse(order));
    }

    public async Task<Result<OrderResponse>> GetOrderByIdAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var cached = await _orderCache.GetAsync(orderId, cancellationToken);
        if (cached is not null)
        {
            return Result.Success(cached);
        }

        var order = await _dbContext.Orders
            .AsNoTracking()
            .Include(o => o.Items)
            .SingleOrDefaultAsync(o => o.Id == orderId, cancellationToken);

        if (order is null)
        {
            return Result.Failure<OrderResponse>(OrderErrors.NotFound(orderId));
        }

        var response = ToResponse(order);
        await _orderCache.SetAsync(response, cancellationToken);
        return Result.Success(response);
    }

    public async Task<IReadOnlyCollection<OrderResponse>> GetOrdersAsync(CancellationToken cancellationToken)
    {
        var orders = await _dbContext.Orders
            .AsNoTracking()
            .Include(o => o.Items)
            .OrderByDescending(o => o.CreatedAtUtc)
            .ToArrayAsync(cancellationToken);

        return orders.Select(ToResponse).ToArray();
    }

    public async Task<Result<OrderResponse>> CancelOrderAsync(Guid orderId, string reason, CancellationToken cancellationToken)
    {
        var order = await _dbContext.Orders
            .Include(o => o.Items)
            .SingleOrDefaultAsync(o => o.Id == orderId, cancellationToken);

        if (order is null)
        {
            return Result.Failure<OrderResponse>(OrderErrors.NotFound(orderId));
        }

        var transition = order.Cancel(_clock.UtcNow);
        if (transition.IsFailure)
        {
            return Result.Failure<OrderResponse>(transition.Error);
        }

        var orderCancelled = new OrderCancelled(order.Id, order.CustomerId, reason);
        var envelope = EventEnvelope.Create(orderCancelled, _correlationContext.CorrelationId, null, OrderServiceDiagnostics.ServiceName, _clock.UtcNow);
        var outboxMessage = OutboxMessage.Create(
            envelope.MessageId,
            envelope.CorrelationId,
            envelope.EventType,
            envelope.EventVersion,
            EventRoutingKeys.OrderCancelled,
            JsonSerializer.Serialize(envelope, JsonOptions),
            envelope.OccurredOnUtc);

        await PersistOrderWithOutboxMessageAsync(order, outboxMessage, cancellationToken);
        await _orderCache.RemoveAsync(order.Id, cancellationToken);
        return Result.Success(ToResponse(order));
    }

    private async Task PersistOrderWithOutboxMessageAsync(OrderAggregate order, OutboxMessage outboxMessage, CancellationToken cancellationToken)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        if (_dbContext.Entry(order).State == EntityState.Detached)
        {
            _dbContext.Orders.Add(order);
        }

        _dbContext.OutboxMessages.Add(outboxMessage);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static OrderResponse ToResponse(OrderAggregate order)
    {
        return new OrderResponse(
            order.Id,
            order.CustomerId,
            order.ClientRequestId,
            order.Status,
            order.TotalAmount,
            order.Currency,
            order.CreatedAtUtc,
            order.Items.Select(item => new OrderItemResponse(item.ProductId, item.Quantity, item.UnitPrice, item.LineTotal)).ToArray());
    }
}
