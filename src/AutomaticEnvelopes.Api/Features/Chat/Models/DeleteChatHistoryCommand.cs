namespace AutomaticEnvelopes.Api.Features.Chat.Models;

public record DeleteChatHistoryCommand(
    string PhoneNumber,
    string TenantId,
    string MessageId,
    string BotPhoneNumberId
);