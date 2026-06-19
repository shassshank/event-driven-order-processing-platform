using Xunit;
using System.Net;
using System.Net.Http.Json;
using BuildingBlocks.Contracts;
using BuildingBlocks.Contracts.Events;
using BuildingBlocks.Persistence;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OrderService.Domain;
using OrderService.Features.Orders;
using OrderService.Persistence;
using Testcontainers.PostgreSql;

namespace OrderService.IntegrationTests;

[Trait("Category", "Docker")]
public sealed class OrderApiTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("orders")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    private CustomOrderServiceFactory _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _factory = new CustomOrderServiceFactory(_postgres.GetConnectionString());
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
            await db.Database.EnsureCreatedAsync();
        }

        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task Post_orders_returns_201_for_valid_request()
    {
        var request = ValidRequest();

        var response = await _client.PostAsJsonAsync("/api/orders", request);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var order = await response.Content.ReadFromJsonAsync<OrderResponse>();
        order.Should().NotBeNull();
        order!.TotalAmount.Should().Be(24.68m);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        db.OutboxMessages.Should().ContainSingle(message =>
            message.EventType == "OrderCreated" &&
            message.RoutingKey == "order.created" &&
            message.Status == OutboxMessageStatus.Pending);
    }

    [Fact]
    public async Task Post_orders_returns_400_for_invalid_request()
    {
        var request = ValidRequest(items: []);

        var response = await _client.PostAsJsonAsync("/api/orders", request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Get_order_returns_404_when_not_found()
    {
        var response = await _client.GetAsync($"/api/orders/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Get_order_returns_order_when_found()
    {
        var create = await _client.PostAsJsonAsync("/api/orders", ValidRequest());
        var created = await create.Content.ReadFromJsonAsync<OrderResponse>();

        var response = await _client.GetAsync($"/api/orders/{created!.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var order = await response.Content.ReadFromJsonAsync<OrderResponse>();
        order!.Id.Should().Be(created.Id);
    }

    [Fact]
    public async Task Cancel_order_returns_200_and_writes_order_cancelled_outbox_message()
    {
        var createResponse = await _client.PostAsJsonAsync("/api/orders", ValidRequest());
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await createResponse.Content.ReadFromJsonAsync<OrderResponse>();
        created.Should().NotBeNull();

        var cancelResponse = await _client.PostAsJsonAsync(
            $"/api/orders/{created!.Id}/cancel",
            new CancelOrderRequest("customer_requested"));

        cancelResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var cancelled = await cancelResponse.Content.ReadFromJsonAsync<OrderResponse>();
        cancelled.Should().NotBeNull();
        cancelled!.Id.Should().Be(created.Id);
        cancelled.Status.Should().Be(OrderStatus.Cancelled);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var storedOrder = db.Orders.Single(order => order.Id == created.Id);
        storedOrder.Status.Should().Be(OrderStatus.Cancelled);
        db.OutboxMessages.Should().ContainSingle(message =>
            message.EventType == nameof(OrderCancelled) &&
            message.RoutingKey == EventRoutingKeys.OrderCancelled &&
            message.Status == OutboxMessageStatus.Pending);
    }

    [Fact]
    public async Task Cancel_order_returns_404_when_order_does_not_exist()
    {
        var response = await _client.PostAsJsonAsync($"/api/orders/{Guid.NewGuid()}/cancel", new CancelOrderRequest("customer request"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private static CreateOrderRequest ValidRequest(IReadOnlyCollection<CreateOrderItemRequest>? items = null)
    {
        return new CreateOrderRequest(
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString("N"),
            "USD",
            items ?? [new CreateOrderItemRequest(Guid.NewGuid().ToString(), 2, 12.34m)]);
    }
}
