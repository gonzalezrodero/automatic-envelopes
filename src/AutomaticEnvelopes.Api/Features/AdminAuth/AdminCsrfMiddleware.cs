using Microsoft.Extensions.Options;

namespace AutomaticEnvelopes.Api.Features.AdminAuth;

/// <summary>
/// Rejects cookie-authenticated state changes on admin and portal routes unless
/// Origin, or Referer when Origin is absent, is one of the configured portal origins.
/// Bearer requests are not cookie-authenticated: the JWT bearer handler ignores the
/// session cookie when Authorization is present.
/// </summary>
public sealed class AdminCsrfMiddleware(
    RequestDelegate next,
    IOptions<AdminAuthOptions> options,
    ILogger<AdminCsrfMiddleware> logger)
{
    private static readonly HashSet<string> UnsafeMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        HttpMethods.Post,
        HttpMethods.Put,
        HttpMethods.Patch,
        HttpMethods.Delete
    };

    public async Task InvokeAsync(HttpContext context)
    {
        if (RequiresOrigin(context.Request) && !HasAllowedOrigin(context.Request))
        {
            logger.LogWarning(
                "Rejected cookie-authenticated {Method} {Path} because Origin is not an allowed portal origin.",
                context.Request.Method,
                context.Request.Path);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        await next(context);
    }

    private static bool RequiresOrigin(HttpRequest request)
    {
        if (!UnsafeMethods.Contains(request.Method) || !IsAdminOrPortal(request.Path))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(request.Headers.Authorization))
        {
            return false;
        }

        return request.Cookies.TryGetValue(AdminAuthCookies.AccessToken, out var accessToken)
            && !string.IsNullOrWhiteSpace(accessToken);
    }

    private bool HasAllowedOrigin(HttpRequest request)
    {
        var origin = request.Headers.Origin.ToString();
        if (string.IsNullOrWhiteSpace(origin))
        {
            origin = OriginOf(request.Headers.Referer.ToString());
        }

        return options.Value.IsAllowedOrigin(origin);
    }

    private static bool IsAdminOrPortal(PathString path)
    {
        if (path.StartsWithSegments("/api/whatsapp"))
        {
            return false;
        }

        return path.StartsWithSegments("/api/admin") || path.StartsWithSegments("/tenants");
    }

    private static string? OriginOf(string? referer)
    {
        if (string.IsNullOrWhiteSpace(referer) || !Uri.TryCreate(referer, UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (string.IsNullOrEmpty(uri.Host))
        {
            return null;
        }

        return uri.GetLeftPart(UriPartial.Authority);
    }
}
