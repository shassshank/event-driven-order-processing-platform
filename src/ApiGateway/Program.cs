using ApiGateway.Gateway;
using BuildingBlocks.Observability;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCorrelationContext();
builder.Services.Configure<DownstreamServiceOptions>(builder.Configuration.GetSection("ServiceUrls"));
builder.Services.Configure<GatewayAuthOptions>(builder.Configuration.GetSection("GatewayAuth"));
builder.Services.AddHttpClient(GatewayProxy.HttpClientName, client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddHealthChecks();
builder.Services.AddServiceTelemetry("ApiGateway");

var app = builder.Build();

app.UseMiddleware<CorrelationIdMiddleware>();

app.MapGet("/health", () => Results.Ok(new { service = "ApiGateway", status = "Healthy" }));

app.MapGet("/health/downstream", async (IHttpClientFactory httpClientFactory, IOptions<DownstreamServiceOptions> downstreamOptions, CancellationToken cancellationToken) =>
{
    var options = downstreamOptions.Value;
    var client = httpClientFactory.CreateClient(GatewayProxy.HttpClientName);
    var results = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

    foreach (var serviceName in new[]
             {
                 GatewayServiceNames.Order,
                 GatewayServiceNames.Inventory,
                 GatewayServiceNames.Payment,
                 GatewayServiceNames.Notification,
                 GatewayServiceNames.Reporting
             })
    {
        if (!options.TryGetBaseUrl(serviceName, out var baseUrl))
        {
            results[serviceName] = new { status = "NotConfigured" };
            continue;
        }

        try
        {
            using var response = await client.GetAsync(new Uri(new Uri(baseUrl.EndsWith("/", StringComparison.Ordinal) ? baseUrl : baseUrl + "/"), "health"), cancellationToken);
            results[serviceName] = new { status = response.IsSuccessStatusCode ? "Healthy" : "Unhealthy", statusCode = (int)response.StatusCode };
        }
        catch (HttpRequestException ex)
        {
            results[serviceName] = new { status = "Unreachable", error = ex.Message };
        }
    }

    return Results.Ok(new { service = "ApiGateway", downstream = results });
});

app.MapGet("/api/gateway/routes", () => GatewayRouteCatalog.Routes.Select(route => new
{
    route.Name,
    route.Method,
    route.GatewayPattern,
    route.DownstreamService,
    route.RequiresAuthentication,
    route.Description
}));

app.MapGatewayProxyRoutes();

app.Run();

public partial class Program;
