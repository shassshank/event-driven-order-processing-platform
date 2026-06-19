using FluentAssertions;
using PaymentService.Persistence;
using Xunit;

namespace PaymentServiceUnitTests;

public sealed class PaymentOrderLockTests
{
    [Fact]
    public void Advisory_lock_key_should_be_deterministic_for_same_order_id()
    {
        var orderId = Guid.NewGuid();

        var first = PaymentOrderLock.ToAdvisoryLockKey(orderId);
        var second = PaymentOrderLock.ToAdvisoryLockKey(orderId);

        second.Should().Be(first);
        second.Should().NotBe(0);
    }

    [Fact]
    public void Advisory_lock_key_should_vary_for_different_order_ids()
    {
        var first = PaymentOrderLock.ToAdvisoryLockKey(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        var second = PaymentOrderLock.ToAdvisoryLockKey(Guid.Parse("22222222-2222-2222-2222-222222222222"));

        first.Should().NotBe(0);
        second.Should().NotBe(0);
        second.Should().NotBe(first);
    }
}
