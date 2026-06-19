using FluentAssertions;
using PaymentService.Domain;
using Xunit;

namespace PaymentServiceUnitTests;

public sealed class PaymentAttemptTests
{
    [Fact]
    public void Complete_Should_mark_payment_completed_once()
    {
        var payment = PaymentAttempt.Create(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            24.68m,
            "usd",
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        payment.Complete("txn-123", new DateTime(2026, 1, 1, 0, 0, 1, DateTimeKind.Utc));
        payment.Complete("txn-456", new DateTime(2026, 1, 1, 0, 0, 2, DateTimeKind.Utc));

        payment.Status.Should().Be(PaymentStatus.Completed);
        payment.ProviderTransactionId.Should().Be("txn-123");
        payment.Amount.Should().Be(24.68m);
        payment.Currency.Should().Be("USD");
    }

    [Fact]
    public void Fail_Should_mark_payment_failed_once()
    {
        var payment = PaymentAttempt.Create(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            24.68m,
            "USD",
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        payment.Fail("provider_declined", "Declined", new DateTime(2026, 1, 1, 0, 0, 1, DateTimeKind.Utc));
        payment.Fail("other", "Other", new DateTime(2026, 1, 1, 0, 0, 2, DateTimeKind.Utc));

        payment.Status.Should().Be(PaymentStatus.Failed);
        payment.FailureCode.Should().Be("provider_declined");
        payment.FailureReason.Should().Be("Declined");
    }
}
