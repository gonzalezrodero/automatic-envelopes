using System.Text.Json.Serialization;

namespace AutomaticEnvelopes.Api.Features.Tenancy;

/// <summary>
/// JSON shape the admin portal already deserializes. Field names match automatic-letters-web.
/// </summary>
public sealed class TenantPortalProfile
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("shortName")]
    public required string ShortName { get; init; }

    [JsonPropertyName("city")]
    public required string City { get; init; }

    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("botPhoneNumberId")]
    public required string BotPhoneNumberId { get; init; }

    [JsonPropertyName("displayPhone")]
    public required string DisplayPhone { get; init; }

    [JsonPropertyName("systemPrompt")]
    public required string SystemPrompt { get; init; }

    [JsonPropertyName("privacyPolicyUrl")]
    public required string PrivacyPolicyUrl { get; init; }

    public static TenantPortalProfile From(TenantProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var display = TenantDisplayDefaults.For(profile.Id);
        return new TenantPortalProfile
        {
            Id = profile.Id,
            Name = First(profile.Name, display.Name),
            ShortName = First(profile.ShortName, display.ShortName),
            City = First(profile.City, display.City),
            Kind = First(profile.Kind, display.Kind),
            BotPhoneNumberId = profile.BotPhoneNumberId ?? string.Empty,
            DisplayPhone = First(profile.DisplayPhone, display.DisplayPhone),
            SystemPrompt = profile.SystemPrompt ?? string.Empty,
            PrivacyPolicyUrl = profile.PrivacyPolicyUrl ?? string.Empty
        };
    }

    private static string First(string? stored, string fallback) =>
        string.IsNullOrWhiteSpace(stored) ? fallback : stored.Trim();
}

public static class TenantDisplayDefaults
{
    public const string ClubBasquetSamaId = "club-basquet-sama";

    public static TenantDisplay For(string? tenantId)
    {
        if (string.Equals(tenantId, ClubBasquetSamaId, StringComparison.Ordinal))
        {
            return ClubBasquetSama;
        }

        var name = Humanize(tenantId);
        return new TenantDisplay(name, name, string.Empty, string.Empty, string.Empty);
    }

    /// <summary>
    /// Labels the portal already shows for the live Samà tenant. Applied only when the stored
    /// document has no display value. The bot keeps using BotPhoneNumberId.
    /// </summary>
    public static TenantDisplay ClubBasquetSama { get; } = new(
        "Club Bàsquet Samà",
        "CB Samà",
        "Cambrils",
        "Campus d\u2019estiu de bàsquet",
        "+34 977 000 214");

    private static string Humanize(string? tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return string.Empty;
        }

        var words = tenantId.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return words.Length == 0
            ? tenantId
            : string.Join(' ', words.Select(Capitalize));
    }

    private static string Capitalize(string word) =>
        char.ToUpperInvariant(word[0]) + word[1..];
}

public readonly record struct TenantDisplay(
    string Name,
    string ShortName,
    string City,
    string Kind,
    string DisplayPhone);
