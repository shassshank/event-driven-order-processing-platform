using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;

namespace BuildingBlocks.Observability;

public static class CorrelationConstants
{
    public const string HeaderName = "X-Correlation-ID";
    public const string LogPropertyName = "CorrelationId";
}

public interface ICorrelationContext
{
    Guid CorrelationId { get; set; }
}

public sealed class CorrelationContext : ICorrelationContext
{
    public Guid CorrelationId { get; set; }
}

public sealed class CorrelationIdMiddleware
{
    private readonly RequestDelegate _next;

    public CorrelationIdMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext httpContext, ICorrelationContext correlationContext)
    {
        var correlationId = ResolveCorrelationId(httpContext.Request.Headers[CorrelationConstants.HeaderName]);
        correlationContext.CorrelationId = correlationId;

        Activity.Current?.SetTag("correlation.id", correlationId.ToString());
        httpContext.Response.Headers[CorrelationConstants.HeaderName] = correlationId.ToString();

        await _next(httpContext);
    }

    private static Guid ResolveCorrelationId(StringValues headerValues)
    {
        var raw = headerValues.FirstOrDefault();
        return Guid.TryParse(raw, out var parsed) ? parsed : Guid.NewGuid();
    }
}

public static class CorrelationExtensions
{
    public static IServiceCollection AddCorrelationContext(this IServiceCollection services)
    {
        services.AddScoped<ICorrelationContext, CorrelationContext>();
        return services;
    }
}
