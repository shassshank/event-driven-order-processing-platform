using BuildingBlocks.EventBus.RabbitMQ;
using FluentAssertions;
using Xunit;

namespace EventBusRabbitMQTests;

public sealed class RoutingKeyValidatorTests
{
    [Fact]
    public void Should_accept_supported_routing_key()
    {
        var act = () => RoutingKeyValidator.ThrowIfUnsupported("order.created");

        act.Should().NotThrow();
    }

    [Fact]
    public void Should_reject_unsupported_routing_key()
    {
        var act = () => RoutingKeyValidator.ThrowIfUnsupported("orders.created.typo");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*not supported*");
    }
}
