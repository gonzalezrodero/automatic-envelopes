using AutomaticEnvelopes.Api.Core.Events;
using Wolverine;
using Wolverine.Persistence.Sagas;

namespace AutomaticEnvelopes.Api.Features.Chat;

// 1. Internal messages used by the Saga
public record ChatWindowExpired([property: SagaIdentity] string PhoneNumber);
public record AnalyzeChatSession(string PhoneNumber, string TenantId, string BotPhoneNumberId, string CombinedText);

public class ChatDebounceSaga : Saga
{
    // Marten uses 'Id' by default as the Primary Key
    public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string BotPhoneNumberId { get; set; } = string.Empty;
    public string CombinedText { get; set; } = string.Empty;

    // --- IDENTIFIER ---
    // Tells Wolverine how to extract the ID from the incoming message
    public static string Identity(MessageReceived message) => message.PhoneNumber;

    // --- CREATION ---
    public OutgoingMessages Starts(MessageReceived message, ILogger<ChatDebounceSaga> logger)
    {
        logger.LogInformation("Starting 10-second debounce window for {PhoneNumber}", message.PhoneNumber);

        // Populate our own properties
        Id = message.PhoneNumber;
        TenantId = message.TenantId;
        BotPhoneNumberId = message.BotPhoneNumberId;
        CombinedText = message.Text;

        var messages = new OutgoingMessages();
        messages.Schedule(new ChatWindowExpired(message.PhoneNumber), DateTimeOffset.UtcNow.AddSeconds(10));

        return messages;
    }

    // --- UPDATE ---
    public void Handle(MessageReceived message, ILogger<ChatDebounceSaga> logger)
    {
        if (CombinedText.Length + message.Text.Length > 4000)
        {
            logger.LogWarning("Saga context length exceeded 4000 chars for {PhoneNumber}. Dropping extra text.", message.PhoneNumber);
            return;
        }

        logger.LogInformation("Appending text to existing saga for {PhoneNumber}", message.PhoneNumber);
        CombinedText += "\n" + message.Text;
    }

    // --- RESOLUTION ---
    public OutgoingMessages Handle(ChatWindowExpired _, ILogger<ChatDebounceSaga> logger)
    {
        logger.LogInformation("Debounce window closed for {PhoneNumber}. Dispatching to AI.", Id);

        var messages = new OutgoingMessages
        {
            new AnalyzeChatSession(Id, TenantId, BotPhoneNumberId, CombinedText)
        };

        MarkCompleted();

        return messages;
    }
}