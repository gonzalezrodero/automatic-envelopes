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

    // --- CREATION ---
    public static async Task<ChatDebounceSaga> StartsAsync(MessageReceived message, IMessageBus bus, ILogger<ChatDebounceSaga> logger)
    {
        logger.LogInformation("Starting 10-second debounce window for {PhoneNumber}", message.PhoneNumber);

        var saga = new ChatDebounceSaga
        {
            Id = message.PhoneNumber,
            TenantId = message.TenantId,
            BotPhoneNumberId = message.BotPhoneNumberId,
            CombinedText = message.Text
        };

        await bus.PublishAsync(new ChatWindowExpired(message.PhoneNumber));

        return saga;
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
    public AnalyzeChatSession Handle(ChatWindowExpired _, ILogger<ChatDebounceSaga> logger)
    {
        logger.LogInformation("Debounce window closed for {PhoneNumber}. Dispatching to AI.", Id);

        MarkCompleted();

        return new AnalyzeChatSession(Id, TenantId, BotPhoneNumberId, CombinedText);
    }
}