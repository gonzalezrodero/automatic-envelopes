using AutomaticEnvelopes.Api.Core.Events;
using JasperFx;
using Wolverine;
using Wolverine.Persistence.Sagas;

namespace AutomaticEnvelopes.Api.Features.Chat;

public record ChatWindowExpired([property: SagaIdentity] string PhoneNumber);
public record AnalyzeChatSession(string PhoneNumber, string TenantId, string BotPhoneNumberId, string CombinedText);

public class ChatDebounceSaga : Saga
{
    [Identity]
    public string Id { get; set; } = string.Empty;

    public string TenantId { get; set; } = string.Empty;
    public string BotPhoneNumberId { get; set; } = string.Empty;
    public string CombinedText { get; set; } = string.Empty;

    public static string Identity(MessageReceived message) => message.PhoneNumber;

    public OutgoingMessages StartsOrHandles(MessageReceived message, ILogger<ChatDebounceSaga> logger)
    {
        Id = message.PhoneNumber;
        var messages = new OutgoingMessages();

        if (string.IsNullOrEmpty(CombinedText))
        {
            CombinedText = message.Text;
        }
        else if (CombinedText.Length + message.Text.Length > 4000)
        {
            logger.LogWarning("Saga context length exceeded 4000 chars for {PhoneNumber}. Dropping extra text.", message.PhoneNumber);
            return messages;
        }
        else
        {
            logger.LogInformation("Appending text to existing saga for {PhoneNumber}", message.PhoneNumber);
            CombinedText += "\n" + message.Text;
        }

        if (string.IsNullOrEmpty(TenantId))
        {
            logger.LogInformation("Starting debounce window for {PhoneNumber} via SQS native delay", message.PhoneNumber);

            TenantId = message.TenantId;
            BotPhoneNumberId = message.BotPhoneNumberId;

            messages.Add(new ChatWindowExpired(message.PhoneNumber));
        }

        return messages;
    }

    public async Task Handle(ChatWindowExpired _, IMessageBus bus, ILogger<ChatDebounceSaga> logger)
    {
        logger.LogInformation("Debounce window closed for {PhoneNumber}. Dispatching to AI.", Id);
        MarkCompleted();
        await bus.InvokeAsync(new AnalyzeChatSession(Id, TenantId, BotPhoneNumberId, CombinedText));
    }
}