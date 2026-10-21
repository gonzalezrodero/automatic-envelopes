namespace AutomaticEnvelopes.Api.Features.Tenancy;

/// <summary>
/// Marten document for one WhatsApp tenant. The bot reads <see cref="Id"/>,
/// <see cref="BotPhoneNumberId"/>, <see cref="SystemPrompt"/>, <see cref="PrivacyPolicyUrl"/>,
/// and <see cref="EnabledTools"/>. Name, short name, city, kind, and display phone are portal labels.
/// Empty display values stay empty. There is no per-tenant default for those labels.
/// A missing <see cref="MonthlyResponseLimit"/> means <see cref="DefaultMonthlyResponseLimit"/>.
/// Null <see cref="EnabledTools"/> was never stored. An empty array means no tools.
/// </summary>
public class TenantProfile
{
    public string Id { get; set; } = null!;

    public string BotPhoneNumberId { get; init; } = null!;

    public string SystemPrompt { get; init; } = string.Empty;

    public string PrivacyPolicyUrl { get; init; } = string.Empty;

    /// <summary>
    /// Tool names this tenant may call. Null on documents saved before the field existed.
    /// </summary>
    public string[]? EnabledTools { get; init; }

    public int? MonthlyResponseLimit { get; init; }

    public string ContactEmail { get; init; } = string.Empty;

    public const int DefaultMonthlyResponseLimit = 1000;

    public int EffectiveMonthlyResponseLimit => MonthlyResponseLimit ?? DefaultMonthlyResponseLimit;

    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    public string Name { get; init; } = string.Empty;

    public string ShortName { get; init; } = string.Empty;

    public string City { get; init; } = string.Empty;

    public string Kind { get; init; } = string.Empty;

    /// <summary>
    /// Number shown in the portal. Not the Meta phone-number id and not used to route WhatsApp.
    /// </summary>
    public string DisplayPhone { get; init; } = string.Empty;

    public TenantProfile WithSettings(string systemPrompt, string privacyPolicyUrl, string[]? enabledTools, int? monthlyResponseLimit, string contactEmail) => new()
    {
        Id = Id,
        BotPhoneNumberId = BotPhoneNumberId,
        SystemPrompt = systemPrompt,
        PrivacyPolicyUrl = privacyPolicyUrl,
        EnabledTools = enabledTools,
        MonthlyResponseLimit = monthlyResponseLimit,
        ContactEmail = contactEmail,
        CreatedAt = CreatedAt,
        Name = Name,
        ShortName = ShortName,
        City = City,
        Kind = Kind,
        DisplayPhone = DisplayPhone
    };
}