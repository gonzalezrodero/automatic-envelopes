using Microsoft.Extensions.Options;

namespace AutomaticEnvelopes.Api.Features.AdminAuth;

public sealed class AdminCsrfMiddleware(RequestDelegate next, IOptions<AdminAuthOptions> options)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (RequiresOrigin(context.Request) && !HasAllowedOrigin(context.Request))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        await next(context);
    }

    private static bool RequiresOrigin(HttpRequest request)
    {
        if (!IsUnsafe(request.Method) || !IsAdminOrPortal(request.Path))
        {
            return false;
        }

        // POST /auth/token is a login. It has no session cookie yet, so the cookie gate below
        // would miss a cross-site code exchange. /auth/logout still checks Origin itself.
        if (request.Path.StartsWithSegments("/auth"))
        {
            return true;
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

    private static bool IsUnsafe(string method) =>
        HttpMethods.IsPost(method)
        || HttpMethods.IsPut(method)
        || HttpMethods.IsPatch(method)
        || HttpMethods.IsDelete(method);

    private static bool IsAdminOrPortal(PathString path) =>
        path.StartsWithSegments("/api/admin")
        || path.StartsWithSegments("/tenants")
        || path.StartsWithSegments("/auth");

    private static string? OriginOf(string? referer) =>
        Uri.TryCreate(referer, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host)
            ? uri.GetLeftPart(UriPartial.Authority)
            : null;
}
