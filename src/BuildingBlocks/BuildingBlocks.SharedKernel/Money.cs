namespace BuildingBlocks.SharedKernel;

public sealed record Money(decimal Amount, string Currency)
{
    public static readonly IReadOnlySet<string> SupportedCurrencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "USD",
        "EUR",
        "GBP"
    };

    public static Money Of(decimal amount, string currency)
    {
        if (amount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "Money cannot be negative.");
        }

        if (!SupportedCurrencies.Contains(currency))
        {
            throw new ArgumentException($"Unsupported currency '{currency}'.", nameof(currency));
        }

        return new Money(decimal.Round(amount, 2, MidpointRounding.AwayFromZero), currency.ToUpperInvariant());
    }
}
