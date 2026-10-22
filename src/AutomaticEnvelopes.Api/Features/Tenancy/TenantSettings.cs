using System.Diagnostics.CodeAnalysis;
using System.Net.Mail;
using System.Text.Json.Serialization;

namespace AutomaticEnvelopes.Api.Features.Tenancy;

public sealed class UpdateTenantSettingsRequest
{
    [JsonPropertyName("systemPrompt")]
    public string? SystemPrompt { get; set; }

    [JsonPropertyName("privacyPolicyUrl")]
    public string? PrivacyPolicyUrl { get; set; }

    [JsonPropertyName("enabledTools")]
    public string[]? EnabledTools { get; set; }

    [JsonPropertyName("monthlyResponseLimit")]
    public int? MonthlyResponseLimit { get; set; }

    [JsonPropertyName("contactEmail")]
    public string? ContactEmail { get; set; }
}

public readonly record struct ValidTenantSettings(string SystemPrompt, string PrivacyPolicyUrl);

public static class TenantSettingsValidation
{
    public const int MaxSystemPromptLength = 20_000;
    public const string PromptRequired = "systemPrompt is required.";
    public const string PromptTooLong = "systemPrompt is too long.";
    public const string UrlRejected = "privacyPolicyUrl must be an https URL, or http on localhost.";
    public const string BodyRequired = "systemPrompt and privacyPolicyUrl are required.";
    public const string ToolsUnknown = "enabledTools contains an unknown tool.";
    public const int MaxMonthlyResponseLimit = 100_000;
    public const string LimitRejected = "monthlyResponseLimit must be between 0 and 100000.";
    public const string EmailRejected = "contactEmail is not a valid email address.";

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

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in requested)
        {
            var trimmed = name?.Trim();
            if (string.IsNullOrEmpty(trimmed) || !knownToolNames.Contains(trimmed, StringComparer.Ordinal))
            {
                enabledTools = null;
                error = ToolsUnknown;
                return false;
            }

            seen.Add(trimmed);
        }

        enabledTools = seen.ToArray();
        error = string.Empty;
        return true;
    }

    public static bool TryReadMonthlyResponseLimit(int? requested, out int? limit, out string error)
    {
        if (requested is null or >= 0 and <= MaxMonthlyResponseLimit)
        {
            limit = requested;
            error = string.Empty;
            return true;
        }

        limit = null;
        error = LimitRejected;
        return false;
    }

    public static bool TryReadContactEmail(string? requested, out string? email, out string error)
    {
        if (requested is null)
        {
            email = null;
            error = string.Empty;
            return true;
        }

        var trimmed = requested.Trim();
        if (trimmed.Length == 0)
        {
            email = string.Empty;
            error = string.Empty;
            return true;
        }

        if (trimmed.Length > 254 || trimmed.Contains('\r') || trimmed.Contains('\n')
            || !MailAddress.TryCreate(trimmed, out var parsed)
            || !string.Equals(parsed.Address, trimmed, StringComparison.OrdinalIgnoreCase)
            || !parsed.Host.Contains('.', StringComparison.Ordinal))
        {
            email = null;
            error = EmailRejected;
            return false;
        }

        email = parsed.Address;
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
