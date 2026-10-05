using System.Text.Json.Serialization;

namespace AutomaticEnvelopes.Api.Features.Tenancy;

/// <summary>
/// JSON shape the admin portal already deserializes. Field names match automatic-letters-web.
/// Blank display fields stay blank. The portal shows the id when a label was not stored.
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
        return new TenantPortalProfile
        {
            Id = profile.Id,
            Name = Stored(profile.Name),
            ShortName = Stored(profile.ShortName),
            City = Stored(profile.City),
            Kind = Stored(profile.Kind),
            BotPhoneNumberId = profile.BotPhoneNumberId ?? string.Empty,
            DisplayPhone = Stored(profile.DisplayPhone),
            SystemPrompt = profile.SystemPrompt ?? string.Empty,
            PrivacyPolicyUrl = profile.PrivacyPolicyUrl ?? string.Empty
        };
    }

    private static string Stored(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
}
