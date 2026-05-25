namespace SamaBot.Api.Features.WhatsAppWebhook;

public record ProcessWhatsAppMessage(string MessageId, string BotPhoneNumberId, string PhoneNumber, string Text, DateTimeOffset Timestamp);
