namespace SamaBot.Api.Features.WhatsAppWebhook;

public class ProcessedMessage
{
    public string Id { get; set; } = string.Empty;
    public DateTimeOffset ProcessedAt { get; set; }
    public string TenantId { get; init; } = null!;
    public string BotPhoneNumberId { get; init; } = null!;
}