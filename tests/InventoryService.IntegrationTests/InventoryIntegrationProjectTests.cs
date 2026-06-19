using FluentAssertions;
using Xunit;
using InventoryService.Persistence;

namespace InventoryService.IntegrationTests;

public sealed class InventoryIntegrationProjectTests
{
    [Fact]
    public void Inventory_service_project_should_be_available_to_integration_tests()
    {
        typeof(InventoryDbContext).Assembly.GetName().Name.Should().Be("InventoryService");
    }
}
