namespace SamaBot.Api.Features.Tenancy;

public class TenantProfile
{
    public string Id { get; init; } = null!;

    public string BotPhoneNumberId { get; init; } = null!;

    public string SystemPrompt { get; init; } = string.Empty;

    public string PrivacyPolicyUrl { get; init; } = string.Empty;

    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}