namespace AutomaticEnvelopes.Api.Features.WhatsAppWebhook.Models;

public record ProcessWhatsAppMessage(
    string MessageId, 
    string BotPhoneNumberId, 
    string PhoneNumber, 
    string Text, 
    DateTimeOffset Timestamp);
