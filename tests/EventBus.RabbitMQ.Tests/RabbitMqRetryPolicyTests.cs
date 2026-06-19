using BuildingBlocks.EventBus.RabbitMQ;
using FluentAssertions;
using Xunit;

namespace EventBusRabbitMQTests;

public sealed class RabbitMqRetryPolicyTests
{
    [Fact]
    public void Missing_retry_count_should_be_treated_as_zero_and_use_first_retry_queue()
    {
        var decision = RabbitMqRetryPolicy.Decide(headers: null);

        decision.ShouldRetry.Should().BeTrue();
        decision.ShouldDeadLetter.Should().BeFalse();
        decision.CurrentRetryCount.Should().Be(0);
        decision.NextRetryCount.Should().Be(1);
        decision.RetryExchangeName.Should().Be(RabbitMqTopology.RetryExchanges.Retry5Seconds);
        decision.Delay.Should().Be(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Second_failure_should_use_thirty_second_retry_queue()
    {
        var headers = new Dictionary<string, object>
        {
            [RabbitMqRetryPolicy.RetryCountHeader] = 1
        };

        var decision = RabbitMqRetryPolicy.Decide(headers);

        decision.ShouldRetry.Should().BeTrue();
        decision.CurrentRetryCount.Should().Be(1);
        decision.NextRetryCount.Should().Be(2);
        decision.RetryExchangeName.Should().Be(RabbitMqTopology.RetryExchanges.Retry30Seconds);
        decision.Delay.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Third_failure_should_use_two_minute_retry_queue()
    {
        var headers = new Dictionary<string, object>
        {
            [RabbitMqRetryPolicy.RetryCountHeader] = 2
        };

        var decision = RabbitMqRetryPolicy.Decide(headers);

        decision.ShouldRetry.Should().BeTrue();
        decision.NextRetryCount.Should().Be(3);
        decision.RetryExchangeName.Should().Be(RabbitMqTopology.RetryExchanges.Retry2Minutes);
        decision.Delay.Should().Be(TimeSpan.FromMinutes(2));
    }

    [Fact]
    public void Max_retry_count_should_dead_letter_message()
    {
        var headers = new Dictionary<string, object>
        {
            [RabbitMqRetryPolicy.RetryCountHeader] = 3
        };

        var decision = RabbitMqRetryPolicy.Decide(headers);

        decision.ShouldRetry.Should().BeFalse();
        decision.ShouldDeadLetter.Should().BeTrue();
        decision.Reason.Should().Contain("Maximum retry count");
    }

    [Fact]
    public void Corrupted_retry_count_should_dead_letter_message()
    {
        var headers = new Dictionary<string, object>
        {
            [RabbitMqRetryPolicy.RetryCountHeader] = "not-a-number"
        };

        var decision = RabbitMqRetryPolicy.Decide(headers);

        decision.ShouldRetry.Should().BeFalse();
        decision.ShouldDeadLetter.Should().BeTrue();
        decision.Reason.Should().Contain("corrupted");
    }
    [Fact]
    public void Should_read_retry_count_from_string_header()
    {
        var headers = new Dictionary<string, object>
        {
            [RabbitMqRetryPolicy.RetryCountHeader] = "2"
        };

        RabbitMqRetryPolicy.GetRetryCountOrZero(headers).Should().Be(2);
    }

    [Fact]
    public void Corrupted_retry_count_for_attempt_visibility_should_return_zero()
    {
        var headers = new Dictionary<string, object>
        {
            [RabbitMqRetryPolicy.RetryCountHeader] = "invalid"
        };

        RabbitMqRetryPolicy.GetRetryCountOrZero(headers).Should().Be(0);
    }

}
