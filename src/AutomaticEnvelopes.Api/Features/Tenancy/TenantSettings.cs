using System.Text.Json.Serialization;

namespace AutomaticEnvelopes.Api.Features.Tenancy;

public sealed class UpdateTenantSettingsRequest
{
    [JsonPropertyName("systemPrompt")]
    public string? SystemPrompt { get; set; }

    [JsonPropertyName("privacyPolicyUrl")]
    public string? PrivacyPolicyUrl { get; set; }
}

public readonly record struct ValidTenantSettings(string SystemPrompt, string PrivacyPolicyUrl);

public static class TenantSettingsValidation
{
    public const int MaxSystemPromptLength = 20_000;
    public const string PromptRequired = "systemPrompt is required.";
    public const string PromptTooLong = "systemPrompt is too long.";
    public const string UrlRejected = "privacyPolicyUrl must be an https URL, or http on localhost.";
    public const string BodyRequired = "systemPrompt and privacyPolicyUrl are required.";

    public static bool TryValidate(
        UpdateTenantSettingsRequest? request,
        out ValidTenantSettings settings,
        out string error)
    {
        settings = default;
        if (request is null)
        {
            error = BodyRequired;
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.SystemPrompt))
        {
            error = PromptRequired;
            return false;
        }

        var prompt = request.SystemPrompt.Trim();
        if (prompt.Length > MaxSystemPromptLength)
        {
            error = PromptTooLong;
            return false;
        }

        if (!PrivacyPolicyUrls.TryNormalize(request.PrivacyPolicyUrl, out var privacyPolicyUrl))
        {
            error = UrlRejected;
            return false;
        }

        settings = new ValidTenantSettings(prompt, privacyPolicyUrl);
        error = string.Empty;
        return true;
    }
}

public static class PrivacyPolicyUrls
{
    private static readonly HashSet<string> LoopbackHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "localhost",
        "127.0.0.1",
        "::1",
        "[::1]"
    };

    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (trimmed.Length > 2048 || !Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo) || !IsAllowed(uri))
        {
            return false;
        }

        if (uri.AbsoluteUri.Length > 2048)
        {
            return false;
        }

        normalized = uri.AbsoluteUri;
        return true;
    }

    private static bool IsAllowed(Uri uri)
    {
        if (uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && IsLoopback(uri);
    }

    private static bool IsLoopback(Uri uri) =>
        LoopbackHosts.Contains(uri.Host) || LoopbackHosts.Contains(uri.IdnHost);
}
