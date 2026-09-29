using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace AutomaticEnvelopes.Api.Features.AdminAuth;

public interface ICognitoTokenClient
{
    Task<CognitoTokenSet> ExchangeAsync(string code, string codeVerifier, string redirectUri, CancellationToken ct);
}

public sealed record CognitoTokenSet(string AccessToken, string IdToken, int ExpiresIn);

public sealed class CognitoAuthException(int statusCode) : Exception
{
    public int StatusCode { get; } = statusCode;
}

public static class CognitoHttpClient
{
    public const string Name = "Cognito";
}

public sealed class CognitoTokenClient(
    IHttpClientFactory httpClientFactory,
    IOptions<AdminAuthOptions> options,
    ILogger<CognitoTokenClient> logger) : ICognitoTokenClient
{
    private readonly AdminAuthOptions auth = options.Value;

    public async Task<CognitoTokenSet> ExchangeAsync(string code, string codeVerifier, string redirectUri, CancellationToken ct)
    {
        if (!auth.IsConfigured)
        {
            logger.LogError("Cognito token exchange is not configured. Set COGNITO_USER_POOL_ID, COGNITO_CLIENT_ID, and COGNITO_DOMAIN.");
            throw new CognitoAuthException(StatusCodes.Status500InternalServerError);
        }

        var http = httpClientFactory.CreateClient(CognitoHttpClient.Name);
        using var request = new HttpRequestMessage(HttpMethod.Post, auth.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = auth.ClientId,
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
                ["code_verifier"] = codeVerifier
            })
        };

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            logger.LogWarning(ex, "Cognito token endpoint could not be reached.");
            throw new CognitoAuthException(StatusCodes.Status502BadGateway);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Cognito token endpoint rejected the authorization code with status {StatusCode}.",
                    (int)response.StatusCode);
                var status = (int)response.StatusCode >= 500
                    ? StatusCodes.Status502BadGateway
                    : StatusCodes.Status401Unauthorized;
                throw new CognitoAuthException(status);
            }

            CognitoTokenPayload? payload;
            try
            {
                payload = await response.Content.ReadFromJsonAsync<CognitoTokenPayload>(cancellationToken: ct);
            }
            catch (JsonException)
            {
                logger.LogWarning("Cognito token endpoint returned a body that was not a token response.");
                throw new CognitoAuthException(StatusCodes.Status502BadGateway);
            }

            if (payload?.AccessToken is not { Length: > 0 } accessToken ||
                payload.IdToken is not { Length: > 0 } idToken)
            {
                logger.LogWarning("Cognito token endpoint returned a response without an access token and an ID token.");
                throw new CognitoAuthException(StatusCodes.Status502BadGateway);
            }

            return new CognitoTokenSet(accessToken, idToken, payload.ExpiresIn);
        }
    }

    private sealed class CognitoTokenPayload
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("id_token")]
        public string? IdToken { get; set; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }
    }
}
