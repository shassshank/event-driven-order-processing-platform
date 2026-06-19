using PaymentService.Domain;

namespace PaymentService.Messaging;

public sealed class PaymentOptions
{
    public PaymentSimulationMode SimulationMode { get; set; } = PaymentSimulationMode.AlwaysSuccess;
    public int TimeoutMilliseconds { get; set; } = 250;
    public int RandomFailurePercentage { get; set; } = 20;
}
