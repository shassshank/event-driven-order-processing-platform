using System.Globalization;
using System.Net.Http.Headers;
using BuildingBlocks.Observability;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Primitives;

namespace ApiGateway.Gateway;

public static class GatewayProxy
{
    public const string HttpClientName = "api-gateway-proxy";

    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection",
        "Keep-Alive",
        "Proxy-Authenticate",
        "Proxy-Authorization",
        "TE",
        "Trailer",
        "Transfer-Encoding",
        "Upgrade",
        "Host"
    };

    public static void MapGatewayProxyRoutes(this IEndpointRouteBuilder endpoints)
    {
        foreach (var route in GatewayRouteCatalog.Routes)
        {
            var capturedRoute = route;
            endpoints.MapMethods(capturedRoute.GatewayPattern, [capturedRoute.Method],
                async context => await ForwardAsync(context, capturedRoute));
        }
    }

    public static async Task ForwardAsync(HttpContext context, GatewayRoute route)
    {
        var authOptions = context.RequestServices.GetRequiredService<IOptions<GatewayAuthOptions>>().Value;
        if (route.RequiresAuthentication && !GatewayAccessPolicy.IsAuthorized(context.Request.Headers, authOptions))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new
            {
                title = "gateway.unauthorized",
                detail = $"This gateway route requires either {authOptions.ApiKeyHeaderName} or Authorization: Bearer <token>."
            }, context.RequestAborted);
            return;
        }

        var downstreamOptions = context.RequestServices.GetRequiredService<IOptions<DownstreamServiceOptions>>().Value;
        if (!downstreamOptions.TryGetBaseUrl(route.DownstreamService, out var baseUrl))
        {
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            await context.Response.WriteAsJsonAsync(new
            {
                title = "gateway.downstream_not_configured",
                detail = $"No downstream URL is configured for {route.DownstreamService}."
            }, context.RequestAborted);
            return;
        }

        var httpClientFactory = context.RequestServices.GetRequiredService<IHttpClientFactory>();
        var correlationContext = context.RequestServices.GetRequiredService<ICorrelationContext>();
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("ApiGateway.Proxy");
        var targetUri = BuildTargetUri(baseUrl, route.DownstreamPathTemplate, context.Request.RouteValues, context.Request.QueryString);

        using var downstreamRequest = CreateDownstreamRequest(context, targetUri, correlationContext.CorrelationId);
        var httpClient = httpClientFactory.CreateClient(HttpClientName);

        try
        {
            using var downstreamResponse = await httpClient.SendAsync(
                downstreamRequest,
                HttpCompletionOption.ResponseHeadersRead,
                context.RequestAborted);

            await CopyDownstreamResponseAsync(context, downstreamResponse);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Gateway failed to reach {DownstreamService} at {TargetUri}", route.DownstreamService, targetUri);
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            await context.Response.WriteAsJsonAsync(new
            {
                title = "gateway.downstream_unavailable",
                detail = $"Gateway could not reach {route.DownstreamService}."
            }, context.RequestAborted);
        }
    }

    private static Uri BuildTargetUri(
        string baseUrl,
        string downstreamPathTemplate,
        RouteValueDictionary routeValues,
        QueryString queryString)
    {
        var path = downstreamPathTemplate;
        foreach (var (key, value) in routeValues)
        {
            if (value is null)
            {
                continue;
            }

            var replacement = Uri.EscapeDataString(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
            path = path.Replace("{" + key + "}", replacement, StringComparison.OrdinalIgnoreCase);
        }

        var normalizedBaseUrl = baseUrl.EndsWith("/", StringComparison.Ordinal) ? baseUrl : baseUrl + "/";
        var relativePath = path.StartsWith("/", StringComparison.Ordinal) ? path[1..] : path;
        return new Uri(new Uri(normalizedBaseUrl), relativePath + queryString.Value);
    }

    private static HttpRequestMessage CreateDownstreamRequest(HttpContext context, Uri targetUri, Guid correlationId)
    {
        var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), targetUri);

        if (HttpMethods.IsPost(context.Request.Method)
            || HttpMethods.IsPut(context.Request.Method)
            || HttpMethods.IsPatch(context.Request.Method))
        {
            request.Content = new StreamContent(context.Request.Body);
        }

        foreach (var header in context.Request.Headers)
        {
            if (HopByHopHeaders.Contains(header.Key))
            {
                continue;
            }

            if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
            {
                request.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
        }

        request.Headers.Remove(CorrelationConstants.HeaderName);
        request.Headers.TryAddWithoutValidation(CorrelationConstants.HeaderName, correlationId.ToString("D"));
        return request;
    }

    private static async Task CopyDownstreamResponseAsync(HttpContext context, HttpResponseMessage downstreamResponse)
    {
        context.Response.StatusCode = (int)downstreamResponse.StatusCode;
        CopyHeaders(downstreamResponse.Headers, context.Response.Headers);
        CopyHeaders(downstreamResponse.Content.Headers, context.Response.Headers);
        context.Response.Headers.Remove("transfer-encoding");

        await downstreamResponse.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
    }

    private static void CopyHeaders(HttpHeaders sourceHeaders, IHeaderDictionary targetHeaders)
    {
        foreach (var header in sourceHeaders)
        {
            if (HopByHopHeaders.Contains(header.Key))
            {
                continue;
            }

            targetHeaders[header.Key] = new StringValues(header.Value.ToArray());
        }
    }
}
