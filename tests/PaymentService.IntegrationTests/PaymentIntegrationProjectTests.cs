using FluentAssertions;
using Xunit;
using PaymentService.Persistence;

namespace PaymentService.IntegrationTests;

public sealed class PaymentIntegrationProjectTests
{
    [Fact]
    public void Payment_service_project_should_be_available_to_integration_tests()
    {
        typeof(PaymentDbContext).Assembly.GetName().Name.Should().Be("PaymentService");
    }
}
