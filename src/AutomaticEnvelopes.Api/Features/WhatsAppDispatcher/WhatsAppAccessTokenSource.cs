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
    public const string ParameterPathPattern = "/automatic-envelopes/whatsapp/tenants/{tenantId}/access-token";
    public static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private readonly Dictionary<string, CacheEntry> cache = new(StringComparer.Ordinal);
    private readonly object gate = new();

    public static string ParameterName(string tenantId) =>
        $"{TenantParameterPrefix}{tenantId}/access-token";

    public async Task<string> GetAccessTokenAsync(string tenantId, CancellationToken ct)
    {
        // Local runs and tests have no SSM_PATH_WHATSAPP. They keep the configured token.
        if (ssm is null)
            return ConfiguredToken();

        if (!TenantIds.IsValid(tenantId))
            throw TokenFailure(tenantId, ParameterPathPattern, "cannot be read because the tenant id is invalid");

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
        }
        catch (ParameterNotFoundException)
        {
            throw TokenFailure(tenantId, parameterName, "is missing");
        }

        throw TokenFailure(tenantId, parameterName, "is empty");
    }

    private string ConfiguredToken()
    {
        var token = options.Value.AccessToken;
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        return token;
    }

    private InvalidOperationException TokenFailure(string? tenantId, string parameterName, string reason)
    {
        var id = tenantId ?? string.Empty;
        var message = $"WhatsApp access token {reason} for tenant '{id}'. Parameter: {parameterName}.";
        logger.LogError("{Message}", message);
        return new InvalidOperationException(message);
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
