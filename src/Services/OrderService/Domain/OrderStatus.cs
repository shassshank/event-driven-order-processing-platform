namespace OrderService.Domain;

public enum OrderStatus
{
    Pending = 0,
    InventoryReserved = 1,
    InventoryFailed = 2,
    PaymentCompleted = 3,
    PaymentFailed = 4,
    Completed = 5,
    Cancelled = 6
}
