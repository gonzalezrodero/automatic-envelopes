using AutomaticEnvelopes.Api.Common;
using AutomaticEnvelopes.Api.Core.Events;
using JasperFx;
using Wolverine;
using Wolverine.Persistence.Sagas;

namespace AutomaticEnvelopes.Api.Features.Chat;

/// <summary>
/// <see cref="PhoneNumber"/> is the Wolverine saga id, not always the WhatsApp number.
/// New windows publish "{tenantId}:{phone}". Payloads already on the system queue
/// still carry the phone alone, which matches saga rows written before this change.
/// </summary>
public record ChatWindowExpired([property: SagaIdentity] string PhoneNumber);

public record AnalyzeChatSession(string PhoneNumber, string TenantId, string BotPhoneNumberId, string CombinedText);

public class ChatDebounceSaga : Saga
{
    [Identity]
    public string Id { get; set; } = string.Empty;

    public string TenantId { get; set; } = string.Empty;
    public string BotPhoneNumberId { get; set; } = string.Empty;

    /// <summary>
    /// WhatsApp number. Empty on documents stored before the composite saga id.
    /// </summary>
    public string PhoneNumber { get; set; } = string.Empty;

    public string CombinedText { get; set; } = string.Empty;

    public static string Identity(MessageReceived message) => message.SagaId;

    public OutgoingMessages StartsOrHandles(MessageReceived message, ILogger<ChatDebounceSaga> logger)
    {
        Id = message.SagaId;
        if (string.IsNullOrEmpty(PhoneNumber))
        {
            PhoneNumber = message.PhoneNumber;
        }

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
        var phone = string.IsNullOrEmpty(PhoneNumber) ? Id : PhoneNumber;
        logger.LogInformation("Debounce window closed for {PhoneNumber}. Dispatching to AI.", LogMask.Phone(phone));
        MarkCompleted();
        await bus.InvokeAsync(new AnalyzeChatSession(phone, TenantId, BotPhoneNumberId, CombinedText));
    }
}
