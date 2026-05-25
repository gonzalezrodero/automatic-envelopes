namespace SamaBot.Api.Features.Chat;

public record DeleteChatHistoryCommand(
    string PhoneNumber,
    string TenantId,
    string MessageId,
    string BotPhoneNumberId
);