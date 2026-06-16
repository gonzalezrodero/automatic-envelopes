namespace AutomaticEnvelopes.Api.Core.Events;

public record ReplyGenerated(
    string MessageId,
    string BotPhoneNumberId,
    string PhoneNumber,
    string Text,
    string TenantId);