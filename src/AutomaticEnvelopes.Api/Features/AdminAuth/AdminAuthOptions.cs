namespace AutomaticEnvelopes.Api.Features.AdminAuth;

public sealed class AdminAuthOptions
{
    public const string OriginsEnv = "ADMIN_PORTAL_ORIGINS";
    public const string RedirectUrisEnv = "COGNITO_ALLOWED_REDIRECT_URIS";

    public string Region { get; init; } = "eu-west-1";
    public string UserPoolId { get; init; } = string.Empty;
    public string ClientId { get; init; } = string.Empty;
    public string Domain { get; init; } = string.Empty;
    public string[] AllowedOrigins { get; init; } = AdminAuthDefaults.Origins;
    public string[] AllowedRedirectUris { get; init; } = AdminAuthDefaults.RedirectUris;

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

    public static AdminAuthOptions FromConfiguration(IConfiguration configuration)
    {
        var region = configuration["AWS_REGION"];
        return new AdminAuthOptions
        {
            Region = string.IsNullOrWhiteSpace(region) ? "eu-west-1" : region.Trim(),
            UserPoolId = configuration["COGNITO_USER_POOL_ID"]?.Trim() ?? string.Empty,
            ClientId = configuration["COGNITO_CLIENT_ID"]?.Trim() ?? string.Empty,
            Domain = configuration["COGNITO_DOMAIN"]?.Trim() ?? string.Empty,
            AllowedOrigins = ReadList(configuration, OriginsEnv, "AdminAuth:AllowedOrigins", AdminAuthDefaults.Origins),
            AllowedRedirectUris = ReadList(configuration, RedirectUrisEnv, "AdminAuth:AllowedRedirectUris", AdminAuthDefaults.RedirectUris)
        };
    }

    private static string[] ReadList(IConfiguration configuration, string envKey, string sectionKey, string[] fallback)
    {
        var raw = configuration[envKey];
        if (!string.IsNullOrWhiteSpace(raw))
        {
            return RejectWildcard(Split(raw), envKey, fallback);
        }

        var configured = configuration.GetSection(sectionKey).Get<string[]>();
        if (configured is { Length: > 0 })
        {
            return RejectWildcard(
                configured.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()).ToArray(),
                sectionKey,
                fallback);
        }

        return fallback;
    }

    private static string[] Split(string raw) =>
        raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string[] RejectWildcard(string[] values, string source, string[] fallback)
    {
        if (values.Any(value => value == "*"))
        {
            throw new InvalidOperationException($"{source} must list exact values and cannot contain '*'.");
        }

        return values.Length == 0 ? fallback : values;
    }

    private static string[] SanitizeOrigins(IEnumerable<string> origins)
    {
        var sanitized = origins
            .Where(origin => !string.IsNullOrWhiteSpace(origin) && origin != "*")
            .Select(origin => origin.Trim().TrimEnd('/'))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return sanitized.Length == 0 ? AdminAuthDefaults.Origins : sanitized;
    }
}

public static class AdminAuthDefaults
{
    public static readonly string[] Origins =
    [
        "http://localhost:5173",
        "https://admin.core-webhook.eu"
    ];

    public static readonly string[] RedirectUris =
    [
        "http://localhost:5173/admin/auth/callback",
        "https://admin.core-webhook.eu/auth/callback"
    ];
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
