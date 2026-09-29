using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace AutomaticEnvelopes.Api.Features.AdminAuth;

public static class AdminAuthCookies
{
    public const string AccessToken = "ae_access";
    public const string IdToken = "ae_id";

    public static void SetSession(HttpResponse response, string accessToken, string idToken, int expiresInSeconds)
    {
        response.Cookies.Append(AccessToken, accessToken, SessionOptions(expiresInSeconds, path: "/"));
        response.Cookies.Append(IdToken, idToken, SessionOptions(expiresInSeconds, path: "/me"));
    }

    public static void Clear(HttpResponse response)
    {
        response.Cookies.Delete(AccessToken, ClearOptions("/"));
        response.Cookies.Delete(IdToken, ClearOptions("/me"));
    }

    public static void ReadAccessToken(MessageReceivedContext context)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrWhiteSpace(authorization))
        {
            return;
        }

        if (context.Request.Cookies.TryGetValue(AccessToken, out var accessToken) &&
            !string.IsNullOrWhiteSpace(accessToken))
        {
            context.Token = accessToken;
        }
    }

    public static CookieOptions SessionOptions(int expiresInSeconds, string path)
    {
        var lifetime = expiresInSeconds > 0 ? expiresInSeconds : 3600;
        return new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Path = path,
            IsEssential = true,
            MaxAge = TimeSpan.FromSeconds(lifetime)
        };
    }

    public static CookieOptions ClearOptions(string path) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Lax,
        Path = path
    };
}
