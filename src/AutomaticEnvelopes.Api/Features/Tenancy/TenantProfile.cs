namespace AutomaticEnvelopes.Api.Features.Tenancy;

/// <summary>
/// Marten document for one WhatsApp tenant. The bot reads <see cref="Id"/>,
/// <see cref="BotPhoneNumberId"/>, <see cref="SystemPrompt"/>, and <see cref="PrivacyPolicyUrl"/> only.
/// Name, short name, city, kind, and display phone are portal labels. Empty values stay empty in the
/// document; the portal DTO fills them when a response is built.
/// </summary>
public class TenantProfile
{
    public string Id { get; set; } = null!;

    public string BotPhoneNumberId { get; init; } = null!;

    public string SystemPrompt { get; init; } = string.Empty;

    public string PrivacyPolicyUrl { get; init; } = string.Empty;

    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    public string Name { get; init; } = string.Empty;

    public string ShortName { get; init; } = string.Empty;

    public string City { get; init; } = string.Empty;

    public string Kind { get; init; } = string.Empty;

    /// <summary>
    /// Number shown in the portal. Not the Meta phone-number id and not used to route WhatsApp.
    /// </summary>
    public string DisplayPhone { get; init; } = string.Empty;

    public TenantProfile WithSettings(string systemPrompt, string privacyPolicyUrl) => new()
    {
        Id = Id,
        BotPhoneNumberId = BotPhoneNumberId,
        SystemPrompt = systemPrompt,
        PrivacyPolicyUrl = privacyPolicyUrl,
        CreatedAt = CreatedAt,
        Name = Name,
        ShortName = ShortName,
        City = City,
        Kind = Kind,
        DisplayPhone = DisplayPhone
    };
}