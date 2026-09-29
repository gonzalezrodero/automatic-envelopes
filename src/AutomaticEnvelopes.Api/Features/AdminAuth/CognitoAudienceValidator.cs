using System.IdentityModel.Tokens.Jwt;
using Microsoft.IdentityModel.Tokens;

namespace AutomaticEnvelopes.Api.Features.AdminAuth;

/// <summary>
/// Cognito access tokens carry <c>client_id</c> and no <c>aud</c> claim.
/// ID tokens are not accepted as API credentials.
/// </summary>
public static class CognitoAudienceValidator
{
    public static bool ValidateAccessToken(IEnumerable<string>? audiences, SecurityToken? securityToken, string? clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId) || securityToken is not JwtSecurityToken jwt)
        {
            return false;
        }

        var tokenUse = jwt.Claims.FirstOrDefault(claim => claim.Type == "token_use")?.Value;
        if (tokenUse != "access")
        {
            return false;
        }

        var tokenClientId = jwt.Claims.FirstOrDefault(claim => claim.Type == "client_id")?.Value;
        return tokenClientId == clientId;
    }
}
