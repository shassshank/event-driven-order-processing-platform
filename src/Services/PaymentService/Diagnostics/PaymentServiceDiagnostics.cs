using System.Diagnostics.Metrics;

namespace PaymentService.Diagnostics;

public static class PaymentServiceDiagnostics
{
    public const string ServiceName = "PaymentService";
    public static readonly Meter Meter = new(ServiceName);
    public static readonly Counter<long> PaymentsCompleted = Meter.CreateCounter<long>("payments_completed_total");
    public static readonly Counter<long> PaymentsFailed = Meter.CreateCounter<long>("payments_failed_total");
}
