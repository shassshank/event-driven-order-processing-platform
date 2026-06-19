namespace ApiGateway.Gateway;

public sealed record GatewayRoute(
    string Name,
    string Method,
    string GatewayPattern,
    string DownstreamService,
    string DownstreamPathTemplate,
    bool RequiresAuthentication,
    string Description);
