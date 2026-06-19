namespace ApiGateway.Gateway;

public sealed class DownstreamServiceOptions
{
    public string OrderService { get; init; } = "http://order-service:8080";

    public string InventoryService { get; init; } = "http://inventory-service:8080";

    public string PaymentService { get; init; } = "http://payment-service:8080";

    public string NotificationService { get; init; } = "http://notification-service:8080";

    public string ReportingService { get; init; } = "http://reporting-service:8080";

    public bool TryGetBaseUrl(string serviceName, out string baseUrl)
    {
        baseUrl = serviceName switch
        {
            GatewayServiceNames.Order => OrderService,
            GatewayServiceNames.Inventory => InventoryService,
            GatewayServiceNames.Payment => PaymentService,
            GatewayServiceNames.Notification => NotificationService,
            GatewayServiceNames.Reporting => ReportingService,
            _ => string.Empty
        };

        return !string.IsNullOrWhiteSpace(baseUrl);
    }
}

public static class GatewayServiceNames
{
    public const string Order = "order-service";
    public const string Inventory = "inventory-service";
    public const string Payment = "payment-service";
    public const string Notification = "notification-service";
    public const string Reporting = "reporting-service";
}
