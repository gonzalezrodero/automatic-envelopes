using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace AutomaticEnvelopes.Api.Features.AdminAuth;

public interface ICognitoSigningKeys
{
    Task<IReadOnlyCollection<SecurityKey>> GetSigningKeysAsync(CancellationToken ct);
}

public interface ICognitoIdTokenReader
{
    Task<AdminUserProfile> ReadAsync(string idToken, CancellationToken ct);
    Task EnsureAccessTokenAsync(string accessToken, CancellationToken ct);
}

public sealed record AdminUserProfile(string Email, string Name, IReadOnlyList<string> Groups);

public sealed class CognitoJwksProvider(
    IHttpClientFactory httpClientFactory,
    IOptions<AdminAuthOptions> options,
    ILogger<CognitoJwksProvider> logger) : ICognitoSigningKeys
{
    private readonly AdminAuthOptions auth = options.Value;
    private readonly SemaphoreSlim refresh = new(1, 1);
    private IReadOnlyCollection<SecurityKey>? keys;
    private DateTimeOffset freshUntil;

    public async Task<IReadOnlyCollection<SecurityKey>> GetSigningKeysAsync(CancellationToken ct)
    {
        if (keys is { Count: > 0 } cached && DateTimeOffset.UtcNow < freshUntil)
        {
            return cached;
        }

        await refresh.WaitAsync(ct);
        try
        {
            if (keys is { Count: > 0 } fresh && DateTimeOffset.UtcNow < freshUntil)
            {
                return fresh;
            }

            var http = httpClientFactory.CreateClient(CognitoHttpClient.Name);
            using var response = await http.GetAsync(auth.JwksUri, ct);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);
            var downloaded = new JsonWebKeySet(json).GetSigningKeys().ToArray();
            if (downloaded.Length == 0)
            {
                throw new CognitoAuthException(StatusCodes.Status502BadGateway);
            }

            keys = downloaded;
            freshUntil = DateTimeOffset.UtcNow.AddHours(6);
            return keys;
        }
        catch (CognitoAuthException)
        {
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or ArgumentException)
        {
            logger.LogWarning(ex, "Could not refresh Cognito signing keys.");
            if (keys is { Count: > 0 } stale)
            {
                return stale;
            }

            throw new CognitoAuthException(StatusCodes.Status502BadGateway);
        }
        finally
        {
            refresh.Release();
        }
    }
}

public sealed class CognitoIdTokenReader(
    ICognitoSigningKeys signingKeys,
    IOptions<AdminAuthOptions> options) : ICognitoIdTokenReader
{
    private readonly AdminAuthOptions auth = options.Value;

    public async Task<AdminUserProfile> ReadAsync(string idToken, CancellationToken ct)
    {
        var principal = await ValidateAsync(idToken, expectedUse: "id", ct);
        var email = principal.FindFirst("email")?.Value;
        if (string.IsNullOrWhiteSpace(email))
        {
            throw new SecurityTokenException("ID token is missing email.");
        }

        var name = principal.FindFirst("name")?.Value;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = email;
        }

        return new AdminUserProfile(email, name.Trim(), CognitoGroupClaims.Read(principal.Claims));
    }

    public async Task EnsureAccessTokenAsync(string accessToken, CancellationToken ct)
    {
        await ValidateAsync(accessToken, expectedUse: "access", ct);
    }

    private async Task<ClaimsPrincipal> ValidateAsync(string token, string expectedUse, CancellationToken ct)
    {
        if (!auth.IsConfigured)
        {
            throw new CognitoAuthException(StatusCodes.Status500InternalServerError);
        }

        var keys = await signingKeys.GetSigningKeysAsync(ct);
        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = auth.Issuer,
            ValidateLifetime = true,
            ValidateAudience = true,
            ValidAudience = expectedUse == "id" ? auth.ClientId : null,
            AudienceValidator = expectedUse == "access"
                ? (audiences, securityToken, _) => CognitoAudienceValidator.ValidateAccessToken(audiences, securityToken, auth.ClientId)
                : null,
            IssuerSigningKeys = keys,
            ClockSkew = TimeSpan.FromMinutes(2),
            NameClaimType = "name",
            RoleClaimType = "cognito:groups"
        };

        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        var principal = handler.ValidateToken(token, parameters, out var validated);
        var tokenUse = (validated as JwtSecurityToken)?.Claims.FirstOrDefault(claim => claim.Type == "token_use")?.Value;
        if (tokenUse != expectedUse)
        {
            throw new SecurityTokenException("Unexpected token_use.");
        }

        return principal;
    }
}

public static class CognitoGroupClaims
{
    public static IReadOnlyList<string> Read(IEnumerable<Claim> claims)
    {
        var groups = new List<string>();
        foreach (var claim in claims)
        {
            if (claim.Type != "cognito:groups" || string.IsNullOrWhiteSpace(claim.Value))
            {
                continue;
            }

            var value = claim.Value.Trim();
            if (value.StartsWith('['))
            {
                try
                {
                    var parsed = JsonSerializer.Deserialize<string[]>(value);
                    if (parsed != null)
                    {
                        groups.AddRange(parsed.Where(group => !string.IsNullOrWhiteSpace(group)));
                    }

                    continue;
                }
                catch (JsonException)
                {
                    // A single group name can legally start with '['. Keep the raw value.
                }
            }

            groups.Add(value);
        }

        return groups.Distinct(StringComparer.Ordinal).ToArray();
    }
}
