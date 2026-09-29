using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Wolverine.Http;

namespace AutomaticEnvelopes.Api.Features.AdminAuth;

public sealed class AdminSessionResponse
{
    [JsonPropertyName("email")]
    public required string Email { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("groups")]
    public required IReadOnlyList<string> Groups { get; init; }

    public static AdminSessionResponse From(AdminUserProfile profile) => new()
    {
        Email = profile.Email,
        Name = profile.Name,
        Groups = profile.Groups
    };
}

public sealed class LogoutResponse
{
    [JsonPropertyName("cognitoLogoutUrl")]
    public string? CognitoLogoutUrl { get; init; }
}

public sealed class TokenExchangeRequest
{
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("codeVerifier")]
    public string? CodeVerifier { get; set; }

    [JsonPropertyName("redirectUri")]
    public string? RedirectUri { get; set; }
}

public class AuthEndpoints
{
    protected AuthEndpoints()
    {
    }

    [AllowAnonymous]
    [WolverinePost("/auth/token")]
    [EnableRateLimiting(AdminAuthPolicies.Auth)]
    public static async Task<IResult> Exchange(
        TokenExchangeRequest? request,
        HttpContext httpContext,
        ICognitoTokenClient cognito,
        ICognitoIdTokenReader idTokens,
        IOptions<AdminAuthOptions> options,
        ILogger<AuthEndpoints> logger,
        CancellationToken ct)
    {
        NoStore(httpContext);

        if (request is null ||
            string.IsNullOrWhiteSpace(request.Code) ||
            string.IsNullOrWhiteSpace(request.CodeVerifier) ||
            string.IsNullOrWhiteSpace(request.RedirectUri))
        {
            return Results.BadRequest(new { Error = "code, codeVerifier, and redirectUri are required." });
        }

        var code = request.Code.Trim();
        var verifier = request.CodeVerifier.Trim();
        var redirectUri = request.RedirectUri.Trim();

        if (code.Length > 2048 || !IsPkceVerifier(verifier))
        {
            return Results.BadRequest(new { Error = "The authorization request was rejected." });
        }

        if (!options.Value.IsAllowedRedirect(redirectUri))
        {
            logger.LogWarning("Rejected token exchange because redirectUri is not in the allowed list.");
            return Results.BadRequest(new { Error = "redirectUri is not allowed." });
        }

        CognitoTokenSet tokenSet;
        AdminUserProfile profile;
        try
        {
            tokenSet = await cognito.ExchangeAsync(code, verifier, redirectUri, ct);
            await idTokens.EnsureAccessTokenAsync(tokenSet.AccessToken, ct);
            profile = await idTokens.ReadAsync(tokenSet.IdToken, ct);
        }
        catch (CognitoAuthException ex)
        {
            logger.LogWarning(ex, "Cognito token exchange failed with status {StatusCode}.", ex.StatusCode);
            return Failure(ex.StatusCode);
        }
        catch (SecurityTokenException ex)
        {
            logger.LogWarning(ex, "Cognito token validation failed during code exchange.");
            return Results.Unauthorized();
        }

        AdminAuthCookies.SetSession(httpContext.Response, tokenSet.AccessToken, tokenSet.IdToken, tokenSet.ExpiresIn);
        return Results.Ok(AdminSessionResponse.From(profile));
    }

    [Authorize]
    [WolverineGet("/me")]
    [EnableRateLimiting(AdminAuthPolicies.Auth)]
    public static async Task<IResult> Me(
        ClaimsPrincipal user,
        HttpContext httpContext,
        ICognitoIdTokenReader idTokens,
        CancellationToken ct)
    {
        NoStore(httpContext);

        if (user.Identity?.IsAuthenticated != true)
        {
            return Results.Unauthorized();
        }

        if (httpContext.Request.Cookies.TryGetValue(AdminAuthCookies.IdToken, out var idToken) &&
            !string.IsNullOrWhiteSpace(idToken))
        {
            try
            {
                var profile = await idTokens.ReadAsync(idToken, ct);
                if (!SubjectsMatch(user, profile.Subject))
                {
                    return Results.Unauthorized();
                }

                return Results.Ok(AdminSessionResponse.From(profile));
            }
            catch (Exception ex) when (ex is SecurityTokenException or CognitoAuthException)
            {
                return Results.Unauthorized();
            }
        }

        var email = user.FindFirst("email")?.Value ?? user.FindFirst(ClaimTypes.Email)?.Value;
        if (string.IsNullOrWhiteSpace(email))
        {
            return Results.Unauthorized();
        }

        var name = user.FindFirst("name")?.Value ?? user.FindFirst(ClaimTypes.Name)?.Value;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = email;
        }

        var groups = CognitoGroupClaims.Read(user.Claims);
        if (groups.Count == 0)
        {
            groups = user.FindAll(ClaimTypes.Role)
                .Select(claim => claim.Value)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }

        return Results.Ok(new AdminSessionResponse
        {
            Email = email,
            Name = name,
            Groups = groups
        });
    }

    [AllowAnonymous]
    [WolverinePost("/auth/logout")]
    [EnableRateLimiting(AdminAuthPolicies.Auth)]
    public static IResult Logout(HttpContext httpContext, IOptions<AdminAuthOptions> options)
    {
        NoStore(httpContext);
        var origin = httpContext.Request.Headers.Origin.ToString();
        if (!options.Value.IsAllowedOrigin(origin))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        AdminAuthCookies.Clear(httpContext.Response);
        return Results.Ok(new LogoutResponse
        {
            CognitoLogoutUrl = options.Value.HostedLogoutUrl(origin)
        });
    }

    private static bool SubjectsMatch(ClaimsPrincipal user, string idSubject)
    {
        if (string.IsNullOrWhiteSpace(idSubject))
        {
            return false;
        }

        var accessSubject = user.FindFirst("sub")?.Value
            ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return string.Equals(accessSubject, idSubject, StringComparison.Ordinal);
    }

    private static void NoStore(HttpContext httpContext)
    {
        httpContext.Response.Headers.CacheControl = "no-store";
    }

    private static IResult Failure(int statusCode) => statusCode switch
    {
        StatusCodes.Status400BadRequest => Results.BadRequest(new { Error = "The authorization code could not be accepted." }),
        StatusCodes.Status401Unauthorized => Results.Unauthorized(),
        StatusCodes.Status500InternalServerError => Results.Json(
            new { Error = "Authentication is not configured." },
            statusCode: StatusCodes.Status500InternalServerError),
        _ => Results.StatusCode(statusCode)
    };

    private static bool IsPkceVerifier(string value)
    {
        if (value.Length is < 43 or > 128)
        {
            return false;
        }

        foreach (var character in value)
        {
            var allowed = character is (>= 'A' and <= 'Z')
                or (>= 'a' and <= 'z')
                or (>= '0' and <= '9')
                or '-' or '.' or '_' or '~';
            if (!allowed)
            {
                return false;
            }
        }

        return true;
    }
}
