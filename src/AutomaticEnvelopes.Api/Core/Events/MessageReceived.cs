namespace AutomaticEnvelopes.Api.Core.Events;

public record MessageReceived(
    string MessageId,
    string PhoneNumber,
    string Text,
    string TenantId,
    string BotPhoneNumberId,
    DateTimeOffset ReceivedAt
);