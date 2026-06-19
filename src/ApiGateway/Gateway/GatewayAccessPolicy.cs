using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace ApiGateway.Gateway;

public static class GatewayAccessPolicy
{
    public static bool IsAuthorized(IHeaderDictionary headers, GatewayAuthOptions options)
    {
        if (headers.TryGetValue(options.ApiKeyHeaderName, out var apiKeyValues)
            && MatchesAny(apiKeyValues, options.ApiKey))
        {
            return true;
        }

        if (!headers.TryGetValue("Authorization", out var authorizationValues))
        {
            return false;
        }

        return authorizationValues.Any(value =>
            value is not null
            && value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            && string.Equals(value["Bearer ".Length..].Trim(), options.BearerToken, StringComparison.Ordinal));
    }

    private static bool MatchesAny(StringValues values, string expected) =>
        !string.IsNullOrWhiteSpace(expected)
        && values.Any(value => string.Equals(value, expected, StringComparison.Ordinal));
}
