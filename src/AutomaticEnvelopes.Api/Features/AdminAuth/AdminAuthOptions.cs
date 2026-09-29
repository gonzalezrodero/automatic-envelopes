namespace AutomaticEnvelopes.Api.Features.AdminAuth;

public sealed class AdminAuthOptions
{
    public const string OriginsEnv = "ADMIN_PORTAL_ORIGINS";
    public const string RedirectUrisEnv = "COGNITO_ALLOWED_REDIRECT_URIS";
    public const string LogoutUrisEnv = "COGNITO_LOGOUT_URIS";

    public string Region { get; init; } = "eu-west-1";
    public string UserPoolId { get; init; } = string.Empty;
    public string ClientId { get; init; } = string.Empty;
    public string Domain { get; init; } = string.Empty;
    public string[] AllowedOrigins { get; init; } = [];
    public string[] AllowedRedirectUris { get; init; } = [];
    public string[] AllowedLogoutUris { get; init; } = [];

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(UserPoolId) &&
        !string.IsNullOrWhiteSpace(ClientId) &&
        !string.IsNullOrWhiteSpace(Domain);

    public string Issuer => $"https://cognito-idp.{Region}.amazonaws.com/{UserPoolId}";

    public string JwksUri => $"{Issuer}/.well-known/jwks.json";

    public string TokenEndpoint => $"https://{Host}/oauth2/token";

    public string[] CorsOrigins => SanitizeOrigins(AllowedOrigins);

    public string Host
    {
        get
        {
            var host = Domain.Trim().TrimEnd('/');
            if (host.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                host = host["https://".Length..];
            }
            else if (host.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                host = host["http://".Length..];
            }

            var slash = host.IndexOf('/');
            return slash >= 0 ? host[..slash] : host;
        }
    }

    public bool IsAllowedRedirect(string redirectUri) =>
        AllowedRedirectUris.Contains(redirectUri, StringComparer.Ordinal);

    public bool IsAllowedOrigin(string? origin) =>
        !string.IsNullOrWhiteSpace(origin) && CorsOrigins.Contains(origin, StringComparer.Ordinal);

    /// <summary>
    /// Cognito Hosted UI logout for the portal origin. Null when this origin has no configured sign-out URL.
    /// Clearing the API cookies does not end the Cognito session; the browser must navigate here.
    /// </summary>
    public string? HostedLogoutUrl(string? requestOrigin)
    {
        var logoutUri = LogoutUriFor(requestOrigin);
        if (logoutUri is null || string.IsNullOrWhiteSpace(Host) || string.IsNullOrWhiteSpace(ClientId))
        {
            return null;
        }

        var query = $"?client_id={Uri.EscapeDataString(ClientId)}&logout_uri={Uri.EscapeDataString(logoutUri)}";
        return new UriBuilder(Uri.UriSchemeHttps, Host, -1, "/logout", query).Uri.AbsoluteUri;
    }

    private string? LogoutUriFor(string? requestOrigin)
    {
        if (string.IsNullOrWhiteSpace(requestOrigin))
        {
            return null;
        }

        return AllowedLogoutUris.Where(logoutUri => OriginOf(logoutUri) == requestOrigin).FirstOrDefault();
    }

    private static string? OriginOf(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || string.IsNullOrEmpty(parsed.Host))
        {
            return null;
        }

        return parsed.GetLeftPart(UriPartial.Authority);
    }

    public static AdminAuthOptions FromConfiguration(IConfiguration configuration)
    {
        var region = configuration["AWS_REGION"];
        return new AdminAuthOptions
        {
            Region = string.IsNullOrWhiteSpace(region) ? "eu-west-1" : region.Trim(),
            UserPoolId = configuration["COGNITO_USER_POOL_ID"]?.Trim() ?? string.Empty,
            ClientId = configuration["COGNITO_CLIENT_ID"]?.Trim() ?? string.Empty,
            Domain = configuration["COGNITO_DOMAIN"]?.Trim() ?? string.Empty,
            AllowedOrigins = ReadList(configuration, OriginsEnv, "AdminAuth:AllowedOrigins"),
            AllowedRedirectUris = ReadList(configuration, RedirectUrisEnv, "AdminAuth:AllowedRedirectUris"),
            AllowedLogoutUris = ReadList(configuration, LogoutUrisEnv, "AdminAuth:AllowedLogoutUris")
        };
    }

    private static string[] ReadList(IConfiguration configuration, string envKey, string sectionKey)
    {
        var raw = configuration[envKey];
        if (!string.IsNullOrWhiteSpace(raw))
        {
            return RejectWildcard(Split(raw), envKey);
        }

        var configured = configuration.GetSection(sectionKey).Get<string[]>();
        if (configured is { Length: > 0 })
        {
            return RejectWildcard(
                configured.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()).ToArray(),
                sectionKey);
        }

        return [];
    }

    private static string[] Split(string raw) =>
        raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string[] RejectWildcard(string[] values, string source)
    {
        if (values.Any(value => value == "*"))
        {
            throw new InvalidOperationException($"{source} must list exact values and cannot contain '*'.");
        }

        return values;
    }

    private static string[] SanitizeOrigins(IEnumerable<string> origins) =>
        origins
            .Where(origin => !string.IsNullOrWhiteSpace(origin) && origin != "*")
            .Select(origin => origin.Trim().TrimEnd('/'))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
}

public static class AdminPortalCors
{
    public const string PolicyName = "AdminPortal";
}

public static class AdminAuthPolicies
{
    public const string Auth = "AuthPolicy";

    /// <summary>
    /// Per caller and path. Kept above AdminPolicy (10/min) so login and GET /me are not throttled with ingest.
    /// </summary>
    public const int PermitLimit = 60;
}
