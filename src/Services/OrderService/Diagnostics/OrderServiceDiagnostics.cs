using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace OrderService.Features.Orders;

public static class OrderServiceDiagnostics
{
    public const string ServiceName = "OrderService";
    public static readonly ActivitySource ActivitySource = new(ServiceName);
    public static readonly Meter Meter = new(ServiceName);
    public static readonly Counter<long> OrdersCreated = Meter.CreateCounter<long>("orders_created_total");
}
