using BuildingBlocks.SharedKernel;

namespace OrderService.Domain;

public sealed class OrderAggregate
{
    private readonly List<OrderItemEntity> _items = [];

    private OrderAggregate()
    {
    }

    private OrderAggregate(
        Guid id,
        Guid customerId,
        string clientRequestId,
        string currency,
        DateTime createdAtUtc)
    {
        Id = id;
        CustomerId = customerId;
        ClientRequestId = clientRequestId;
        Currency = currency;
        Status = OrderStatus.Pending;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; }
    public Guid CustomerId { get; private set; }
    public string ClientRequestId { get; private set; } = string.Empty;
    public OrderStatus Status { get; private set; }
    public decimal TotalAmount { get; private set; }
    public string Currency { get; private set; } = "USD";
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }
    public IReadOnlyCollection<OrderItemEntity> Items => _items.AsReadOnly();

    public static OrderAggregate Create(
        Guid customerId,
        string clientRequestId,
        IEnumerable<(Guid ProductId, int Quantity, decimal UnitPrice)> items,
        string currency,
        DateTime createdAtUtc)
    {
        var order = new OrderAggregate(Guid.NewGuid(), customerId, clientRequestId, currency.ToUpperInvariant(), createdAtUtc);

        foreach (var item in items)
        {
            order._items.Add(OrderItemEntity.Create(order.Id, item.ProductId, item.Quantity, item.UnitPrice));
        }

        order.TotalAmount = Money.Of(order._items.Sum(item => item.LineTotal), order.Currency).Amount;
        return order;
    }

    public Result ApplyStatusTransition(OrderStatus nextStatus, DateTime utcNow)
    {
        if (!IsValidTransition(Status, nextStatus))
        {
            return Result.Failure(OrderErrors.InvalidTransition(Status, nextStatus));
        }

        Status = nextStatus;
        UpdatedAtUtc = utcNow;
        return Result.Success();
    }

    public Result Cancel(DateTime utcNow)
    {
        if (Status == OrderStatus.Completed)
        {
            return Result.Failure(OrderErrors.AlreadyCompleted(Id));
        }

        if (Status == OrderStatus.PaymentCompleted)
        {
            return Result.Failure(OrderErrors.PaymentProcessing(Id));
        }

        return ApplyStatusTransition(OrderStatus.Cancelled, utcNow);
    }

    public static bool IsValidTransition(OrderStatus current, OrderStatus next) => (current, next) switch
    {
        (OrderStatus.Pending, OrderStatus.InventoryReserved) => true,
        (OrderStatus.Pending, OrderStatus.InventoryFailed) => true,
        (OrderStatus.Pending, OrderStatus.Cancelled) => true,
        (OrderStatus.InventoryReserved, OrderStatus.PaymentCompleted) => true,
        (OrderStatus.InventoryReserved, OrderStatus.PaymentFailed) => true,
        (OrderStatus.InventoryReserved, OrderStatus.Cancelled) => true,
        (OrderStatus.PaymentCompleted, OrderStatus.Completed) => true,
        _ => false
    };
}
