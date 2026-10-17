using AutomaticEnvelopes.Api.Common;
using AutomaticEnvelopes.Api.Core.Events;
using JasperFx;
using Wolverine;
using Wolverine.Persistence.Sagas;

namespace AutomaticEnvelopes.Api.Features.Chat;

/// <summary>
/// <see cref="SagaId"/> is <c>{tenantId}:{phone}</c> from <see cref="ChatSagaIds.For"/>.
/// </summary>
public record ChatWindowExpired([property: SagaIdentity] string SagaId);

public record AnalyzeChatSession(string PhoneNumber, string TenantId, string BotPhoneNumberId, string CombinedText);

public class ChatDebounceSaga : Saga
{
    [Identity]
    public string Id { get; set; } = string.Empty;

    public string TenantId { get; set; } = string.Empty;
    public string BotPhoneNumberId { get; set; } = string.Empty;
    public string PhoneNumber { get; set; } = string.Empty;
    public string CombinedText { get; set; } = string.Empty;

    public OutgoingMessages StartsOrHandles(MessageReceived message, ILogger<ChatDebounceSaga> logger)
    {
        Id = message.SagaId;
        PhoneNumber = message.PhoneNumber;

        var messages = new OutgoingMessages();
        var maskedPhone = LogMask.Phone(message.PhoneNumber);

        if (string.IsNullOrEmpty(CombinedText))
        {
            CombinedText = message.Text;
        }
        else if (CombinedText.Length + message.Text.Length > 4000)
        {
            logger.LogWarning("Saga context length exceeded 4000 chars for {PhoneNumber}. Dropping extra text.", maskedPhone);
            return messages;
        }
        else
        {
            logger.LogInformation("Appending text to existing saga for {PhoneNumber}", maskedPhone);
            CombinedText += "\n" + message.Text;
        }

        if (string.IsNullOrEmpty(TenantId))
        {
            logger.LogInformation("Starting debounce window for {PhoneNumber} via SQS native delay", maskedPhone);

            TenantId = message.TenantId;
            BotPhoneNumberId = message.BotPhoneNumberId;

            messages.Add(new ChatWindowExpired(message.SagaId));
        }

        return messages;
    }

    public async Task Handle(ChatWindowExpired _, IMessageBus bus, ILogger<ChatDebounceSaga> logger)
    {
        logger.LogInformation("Debounce window closed for {PhoneNumber}. Dispatching to AI.", LogMask.Phone(PhoneNumber));
        MarkCompleted();
        await bus.InvokeAsync(new AnalyzeChatSession(PhoneNumber, TenantId, BotPhoneNumberId, CombinedText));
    }
}
