using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;
using AutomaticEnvelopes.Api.Common.Configuration;
using AutomaticEnvelopes.Api.Features.Tenancy;
using Microsoft.Extensions.Options;

namespace AutomaticEnvelopes.Api.Features.WhatsAppDispatcher;

public interface IWhatsAppAccessTokenSource
{
    Task<string> GetAccessTokenAsync(string tenantId, CancellationToken ct);
}

/// <summary>
/// Reads <c>/automatic-envelopes/whatsapp/tenants/{tenantId}/access-token</c> when a reply is sent.
/// The Graph phone number id is the tenant document's <c>BotPhoneNumberId</c>, not an SSM parameter.
/// </summary>
public sealed class WhatsAppAccessTokenSource(
    IAmazonSimpleSystemsManagement? ssm,
    IOptions<WhatsAppOptions> options,
    ILogger<WhatsAppAccessTokenSource> logger,
    TimeProvider? time = null) : IWhatsAppAccessTokenSource
{
    public const string TenantParameterPrefix = "/automatic-envelopes/whatsapp/tenants/";
    public static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private readonly Dictionary<string, CacheEntry> cache = new(StringComparer.Ordinal);
    private readonly object gate = new();

    public static string ParameterName(string tenantId) =>
        $"{TenantParameterPrefix}{tenantId}/access-token";

    public async Task<string> GetAccessTokenAsync(string tenantId, CancellationToken ct)
    {
        // Local runs and tests have no SSM_PATH_WHATSAPP. They keep the token from configuration.
        if (ssm is null)
            return SharedToken();

        if (!TenantIds.IsValid(tenantId))
        {
            logger.LogWarning(
                "Tenant id cannot be used as an SSM path segment. Using the shared WhatsApp access token.");
            return SharedToken();
        }

        var parameterName = ParameterName(tenantId);
        if (TryGetCached(parameterName, out var cached))
            return cached;

        var token = await LoadAsync(ssm, tenantId, parameterName, ct);
        Remember(parameterName, token);
        return token;
    }

    private async Task<string> LoadAsync(
        IAmazonSimpleSystemsManagement client,
        string tenantId,
        string parameterName,
        CancellationToken ct)
    {
        try
        {
            var response = await client.GetParameterAsync(new GetParameterRequest
            {
                Name = parameterName,
                WithDecryption = true
            }, ct);

            var value = response.Parameter?.Value;
            if (!string.IsNullOrWhiteSpace(value))
            {
                logger.LogInformation(
                    "Loaded per-tenant WhatsApp access token. TenantId: {TenantId}",
                    tenantId);
                return value;
            }

            logger.LogWarning(
                "Per-tenant WhatsApp access token parameter is empty. Using the shared access token. TenantId: {TenantId}",
                tenantId);
        }
        catch (ParameterNotFoundException)
        {
            logger.LogInformation(
                "Per-tenant WhatsApp access token is not in SSM. Using the shared access token. TenantId: {TenantId}",
                tenantId);
        }

        return SharedToken();
    }

    private string SharedToken()
    {
        var token = options.Value.AccessToken;
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        return token;
    }

    private bool TryGetCached(string parameterName, out string token)
    {
        var now = clock.GetUtcNow();
        lock (gate)
        {
            if (cache.TryGetValue(parameterName, out var entry) && entry.ExpiresAt > now)
            {
                token = entry.Token;
                return true;
            }

            cache.Remove(parameterName);
        }

        token = string.Empty;
        return false;
    }

    private void Remember(string parameterName, string token)
    {
        var expiresAt = clock.GetUtcNow().Add(CacheDuration);
        lock (gate)
        {
            cache[parameterName] = new CacheEntry(token, expiresAt);
        }
    }

    private sealed record CacheEntry(string Token, DateTimeOffset ExpiresAt);
}
