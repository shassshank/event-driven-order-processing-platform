using Microsoft.AspNetCore.Http;

namespace ApiGateway.Gateway;

public static class GatewayRouteCatalog
{
    public static readonly IReadOnlyList<GatewayRoute> Routes =
    [
        new(
            "orders-list",
            HttpMethods.Get,
            "/api/orders",
            GatewayServiceNames.Order,
            "/api/orders",
            RequiresAuthentication: false,
            "List recent orders through OrderService."),
        new(
            "orders-get",
            HttpMethods.Get,
            "/api/orders/{id:guid}",
            GatewayServiceNames.Order,
            "/api/orders/{id}",
            RequiresAuthentication: false,
            "Get an order by id through OrderService."),
        new(
            "orders-create",
            HttpMethods.Post,
            "/api/orders",
            GatewayServiceNames.Order,
            "/api/orders",
            RequiresAuthentication: true,
            "Create an order through OrderService. Requires demo gateway authentication."),
        new(
            "orders-cancel",
            HttpMethods.Post,
            "/api/orders/{id:guid}/cancel",
            GatewayServiceNames.Order,
            "/api/orders/{id}/cancel",
            RequiresAuthentication: true,
            "Cancel an order through OrderService. Requires demo gateway authentication."),
        new(
            "products-list",
            HttpMethods.Get,
            "/api/products",
            GatewayServiceNames.Inventory,
            "/api/products",
            RequiresAuthentication: false,
            "List inventory products through InventoryService."),
        new(
            "products-get",
            HttpMethods.Get,
            "/api/products/{id:guid}",
            GatewayServiceNames.Inventory,
            "/api/products/{id}",
            RequiresAuthentication: false,
            "Get an inventory product through InventoryService."),
        new(
            "payments-list",
            HttpMethods.Get,
            "/api/payments",
            GatewayServiceNames.Payment,
            "/api/payments",
            RequiresAuthentication: false,
            "List payment attempts through PaymentService."),
        new(
            "payments-for-order",
            HttpMethods.Get,
            "/api/payments/orders/{orderId:guid}",
            GatewayServiceNames.Payment,
            "/api/payments/orders/{orderId}",
            RequiresAuthentication: false,
            "Get payment attempt for one order through PaymentService."),
        new(
            "payment-cancellations",
            HttpMethods.Get,
            "/api/payments/cancellations",
            GatewayServiceNames.Payment,
            "/api/payments/cancellations",
            RequiresAuthentication: false,
            "List payment cancellation markers through PaymentService."),
        new(
            "payment-cancellation-for-order",
            HttpMethods.Get,
            "/api/payments/orders/{orderId:guid}/cancellation",
            GatewayServiceNames.Payment,
            "/api/payments/orders/{orderId}/cancellation",
            RequiresAuthentication: false,
            "Get payment cancellation marker for one order through PaymentService."),
        new(
            "notifications-list",
            HttpMethods.Get,
            "/api/notifications",
            GatewayServiceNames.Notification,
            "/api/notifications",
            RequiresAuthentication: false,
            "List notification messages through NotificationService."),
        new(
            "notifications-for-order",
            HttpMethods.Get,
            "/api/notifications/orders/{orderId:guid}",
            GatewayServiceNames.Notification,
            "/api/notifications/orders/{orderId}",
            RequiresAuthentication: false,
            "List notification messages for one order through NotificationService."),
        new(
            "reports-orders",
            HttpMethods.Get,
            "/api/reports/orders",
            GatewayServiceNames.Reporting,
            "/api/reports/orders",
            RequiresAuthentication: false,
            "List reporting read-model orders through ReportingService."),
        new(
            "reports-order",
            HttpMethods.Get,
            "/api/reports/orders/{orderId:guid}",
            GatewayServiceNames.Reporting,
            "/api/reports/orders/{orderId}",
            RequiresAuthentication: false,
            "Get one reporting read-model order through ReportingService."),
        new(
            "reports-events",
            HttpMethods.Get,
            "/api/reports/events",
            GatewayServiceNames.Reporting,
            "/api/reports/events",
            RequiresAuthentication: false,
            "List reporting event audit rows through ReportingService."),
        new(
            "reports-order-events",
            HttpMethods.Get,
            "/api/reports/orders/{orderId:guid}/events",
            GatewayServiceNames.Reporting,
            "/api/reports/orders/{orderId}/events",
            RequiresAuthentication: false,
            "List reporting event audit rows for one order through ReportingService.")
    ];
}
