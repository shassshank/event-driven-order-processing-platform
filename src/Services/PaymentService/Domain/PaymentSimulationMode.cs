namespace PaymentService.Domain;

public enum PaymentSimulationMode
{
    AlwaysSuccess = 0,
    AlwaysFail = 1,
    FailFirstThenSucceed = 2,
    Timeout = 3,
    RandomFailure = 4
}
