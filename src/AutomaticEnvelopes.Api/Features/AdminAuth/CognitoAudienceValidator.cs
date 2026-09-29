using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AutomaticEnvelopes.Api.Features.AdminAuth;

/// <summary>
/// Cognito access tokens carry <c>client_id</c> and no <c>aud</c> claim.
/// ID tokens are not accepted as API credentials.
/// JwtBearer on .NET 10 validates with <see cref="JsonWebTokenHandler"/>, so the
/// token is a <see cref="JsonWebToken"/>. <see cref="JwtSecurityTokenHandler"/> still
/// produces a <see cref="JwtSecurityToken"/>. Both must be accepted.
/// </summary>
public static class CognitoAudienceValidator
{
    public static bool ValidateAccessToken(IEnumerable<string>? audiences, SecurityToken? securityToken, string? clientId)
    {
        // Cognito access tokens have no aud. The delegate still receives the audience list.
        _ = audiences;

        if (string.IsNullOrWhiteSpace(clientId) || !TryReadClaims(securityToken, out var tokenUse, out var tokenClientId))
        {
            return false;
        }

        return tokenUse == "access" && tokenClientId == clientId;
    }

    private static bool TryReadClaims(SecurityToken? securityToken, out string? tokenUse, out string? tokenClientId)
    {
        tokenUse = null;
        tokenClientId = null;
        switch (securityToken)
        {
            case JsonWebToken json:
                tokenUse = Payload(json, "token_use");
                tokenClientId = Payload(json, "client_id");
                return true;
            case JwtSecurityToken jwt:
                tokenUse = Claim(jwt.Claims, "token_use");
                tokenClientId = Claim(jwt.Claims, "client_id");
                return true;
            default:
                return false;
        }
    }

    private static string? Payload(JsonWebToken token, string name) =>
        token.TryGetPayloadValue<string>(name, out var value) ? value : null;

    private static string? Claim(IEnumerable<Claim> claims, string type) =>
        claims.FirstOrDefault(claim => claim.Type == type)?.Value;
}
