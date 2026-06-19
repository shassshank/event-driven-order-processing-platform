namespace ApiGateway.Gateway;

public sealed class GatewayAuthOptions
{
    public string ApiKeyHeaderName { get; init; } = "X-Demo-Api-Key";

    public string ApiKey { get; init; } = "local-dev-key";

    public string BearerToken { get; init; } = "local-dev-token";
}
