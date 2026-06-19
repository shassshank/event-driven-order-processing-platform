using BuildingBlocks.SharedKernel;

namespace OrderService.Domain;

public static class OrderErrors
{
    public static Error NotFound(Guid orderId) => new("orders.not_found", $"Order '{orderId}' was not found.");
    public static Error DuplicateClientRequestId(string clientRequestId) => new("orders.duplicate_client_request_id", $"Client request '{clientRequestId}' has already been used.");
    public static Error InvalidTransition(OrderStatus from, OrderStatus to) => new("orders.invalid_transition", $"Cannot transition order from '{from}' to '{to}'.");
    public static Error AlreadyCompleted(Guid orderId) => new("orders.already_completed", $"Order '{orderId}' is already completed and cannot be cancelled.");
    public static Error PaymentProcessing(Guid orderId) => new("orders.payment_processing", $"Order '{orderId}' cannot be cancelled while payment is processing.");
}
