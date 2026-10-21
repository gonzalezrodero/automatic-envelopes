using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace AutomaticEnvelopes.Api.Features.Tenancy;

public sealed class UpdateTenantSettingsRequest
{
    [JsonPropertyName("systemPrompt")]
    public string? SystemPrompt { get; set; }

    [JsonPropertyName("privacyPolicyUrl")]
    public string? PrivacyPolicyUrl { get; set; }

    /// <summary>
    /// Null when the client omitted the field. An empty array clears every tool.
    /// </summary>
    [JsonPropertyName("enabledTools")]
    public string[]? EnabledTools { get; set; }
}

public readonly record struct ValidTenantSettings(string SystemPrompt, string PrivacyPolicyUrl);

public static class TenantSettingsValidation
{
    public const int MaxSystemPromptLength = 20_000;
    public const string PromptRequired = "systemPrompt is required.";
    public const string PromptTooLong = "systemPrompt is too long.";
    public const string UrlRejected = "privacyPolicyUrl must be an https URL, or http on localhost.";
    public const string BodyRequired = "systemPrompt and privacyPolicyUrl are required.";
    public const int MaxEnabledTools = 16;
    public const string ToolsUnknown = "enabledTools contains an unknown tool.";
    public const string ToolsTooMany = "enabledTools has too many entries.";

    public static bool TryValidate(
        [NotNullWhen(true)] UpdateTenantSettingsRequest? request,
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

    /// <summary>
    /// Null <paramref name="requested"/> means the field was omitted and the stored list stays.
    /// An empty array clears the list. Names must match a registered tool.
    /// </summary>
    public static bool TryReadEnabledTools(
        string[]? requested,
        IEnumerable<string> knownToolNames,
        out string[]? enabledTools,
        out string error)
    {
        if (requested is null)
        {
            enabledTools = null;
            error = string.Empty;
            return true;
        }

        if (requested.Length > MaxEnabledTools)
        {
            enabledTools = null;
            error = ToolsTooMany;
            return false;
        }

        var known = new HashSet<string>(knownToolNames, StringComparer.Ordinal);
        var normalized = new List<string>(requested.Length);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in requested)
        {
            var trimmed = name?.Trim();
            if (string.IsNullOrEmpty(trimmed) || !known.Contains(trimmed))
            {
                enabledTools = null;
                error = ToolsUnknown;
                return false;
            }

            if (seen.Add(trimmed))
                normalized.Add(trimmed);
        }

        enabledTools = normalized.ToArray();
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
