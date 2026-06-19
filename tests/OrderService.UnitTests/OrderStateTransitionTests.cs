using Xunit;
using FluentAssertions;
using OrderService.Domain;

namespace OrderService.UnitTests;

public sealed class OrderStateTransitionTests
{
    [Theory]
    [InlineData(OrderStatus.Pending, OrderStatus.InventoryReserved)]
    [InlineData(OrderStatus.Pending, OrderStatus.InventoryFailed)]
    [InlineData(OrderStatus.Pending, OrderStatus.Cancelled)]
    [InlineData(OrderStatus.InventoryReserved, OrderStatus.PaymentCompleted)]
    [InlineData(OrderStatus.InventoryReserved, OrderStatus.PaymentFailed)]
    [InlineData(OrderStatus.InventoryReserved, OrderStatus.Cancelled)]
    [InlineData(OrderStatus.PaymentCompleted, OrderStatus.Completed)]
    public void Should_enforce_valid_order_state_transitions(OrderStatus current, OrderStatus next)
    {
        OrderAggregate.IsValidTransition(current, next).Should().BeTrue();
    }

    [Theory]
    [InlineData(OrderStatus.Completed, OrderStatus.Cancelled)]
    [InlineData(OrderStatus.Cancelled, OrderStatus.Completed)]
    [InlineData(OrderStatus.InventoryFailed, OrderStatus.PaymentCompleted)]
    [InlineData(OrderStatus.PaymentFailed, OrderStatus.Completed)]
    public void Should_reject_invalid_order_state_transitions(OrderStatus current, OrderStatus next)
    {
        OrderAggregate.IsValidTransition(current, next).Should().BeFalse();
    }

    [Fact]
    public void Should_ignore_duplicate_final_state_events_by_rejecting_repeated_completion_transition()
    {
        OrderAggregate.IsValidTransition(OrderStatus.Completed, OrderStatus.Completed).Should().BeFalse();
    }
}
