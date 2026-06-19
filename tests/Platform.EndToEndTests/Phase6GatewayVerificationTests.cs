using ApiGateway.Gateway;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace PlatformEndToEndTests;

public sealed class Phase6GatewayVerificationTests
{
    [Fact]
    public void Gateway_catalog_should_include_phase6_routes()
    {
        GatewayRouteCatalog.Routes.Should().Contain(route =>
            route.Name == "orders-create"
            && route.Method == HttpMethods.Post
            && route.GatewayPattern == "/api/orders"
            && route.RequiresAuthentication);

        GatewayRouteCatalog.Routes.Should().Contain(route =>
            route.Name == "orders-cancel"
            && route.Method == HttpMethods.Post
            && route.GatewayPattern == "/api/orders/{id:guid}/cancel"
            && route.RequiresAuthentication);

        GatewayRouteCatalog.Routes.Should().Contain(route =>
            route.Name == "payments-list"
            && route.DownstreamService == GatewayServiceNames.Payment);

        GatewayRouteCatalog.Routes.Should().Contain(route =>
            route.Name == "payment-cancellations"
            && route.DownstreamService == GatewayServiceNames.Payment);

        GatewayRouteCatalog.Routes.Should().Contain(route =>
            route.Name == "notifications-list"
            && route.DownstreamService == GatewayServiceNames.Notification);

        GatewayRouteCatalog.Routes.Should().Contain(route =>
            route.Name == "reports-events"
            && route.DownstreamService == GatewayServiceNames.Reporting);
    }

    [Fact]
    public void Gateway_mutating_routes_should_require_authentication()
    {
        GatewayRouteCatalog.Routes
            .Where(route => route.Method == HttpMethods.Post)
            .Should()
            .OnlyContain(route => route.RequiresAuthentication);
    }

    [Fact]
    public void Gateway_access_policy_should_reject_missing_credentials()
    {
        var headers = new HeaderDictionary();

        GatewayAccessPolicy.IsAuthorized(headers, new GatewayAuthOptions()).Should().BeFalse();
    }

    [Fact]
    public void Gateway_access_policy_should_accept_demo_api_key()
    {
        var options = new GatewayAuthOptions { ApiKey = "test-key" };
        var headers = new HeaderDictionary
        {
            [options.ApiKeyHeaderName] = "test-key"
        };

        GatewayAccessPolicy.IsAuthorized(headers, options).Should().BeTrue();
    }

    [Fact]
    public void Gateway_access_policy_should_accept_demo_bearer_token()
    {
        var options = new GatewayAuthOptions { BearerToken = "test-token" };
        var headers = new HeaderDictionary
        {
            ["Authorization"] = "Bearer test-token"
        };

        GatewayAccessPolicy.IsAuthorized(headers, options).Should().BeTrue();
    }

    [Fact]
    public void Downstream_options_should_resolve_configured_service_urls()
    {
        var options = new DownstreamServiceOptions
        {
            OrderService = "http://orders.local",
            InventoryService = "http://inventory.local",
            PaymentService = "http://payments.local",
            NotificationService = "http://notifications.local",
            ReportingService = "http://reporting.local"
        };

        options.TryGetBaseUrl(GatewayServiceNames.Order, out var orderUrl).Should().BeTrue();
        orderUrl.Should().Be("http://orders.local");
        options.TryGetBaseUrl(GatewayServiceNames.Payment, out var paymentUrl).Should().BeTrue();
        paymentUrl.Should().Be("http://payments.local");
        options.TryGetBaseUrl(GatewayServiceNames.Reporting, out var reportingUrl).Should().BeTrue();
        reportingUrl.Should().Be("http://reporting.local");
        options.TryGetBaseUrl("unknown", out _).Should().BeFalse();
    }
}
