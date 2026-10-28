using AutomaticEnvelopes.Api.Common;
using AutomaticEnvelopes.Api.Core.Events;
using JasperFx;
using Marten;
using Wolverine;

namespace AutomaticEnvelopes.Api.Features.Chat;

public record ChatWindowExpired(string SagaId, long WindowStartedAtTicks);

public record AnalyzeChatSession(string PhoneNumber, string TenantId, string BotPhoneNumberId, string CombinedText);

public class ChatDebounceSaga : Saga, IRevisioned
{
    public static readonly TimeSpan StuckAfter = TimeSpan.FromMinutes(10);

    [Identity]
    public string Id { get; set; } = string.Empty;

    public string TenantId { get; set; } = string.Empty;
    public string BotPhoneNumberId { get; set; } = string.Empty;
    public string PhoneNumber { get; set; } = string.Empty;
    public string CombinedText { get; set; } = string.Empty;

    /// <summary>When this debounce window opened. Missing on rows written before the field existed.</summary>
    public DateTimeOffset StartedAt { get; set; }

    public async Task<OutgoingMessages> StartsOrHandles(
        MessageReceived message,
        IDocumentSession session,
        ILogger<ChatDebounceSaga> logger,
        CancellationToken ct)
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
            StartedAt = DateTimeOffset.UtcNow;
            // The previous window's completion marker would block this window's expiry.
            session.Delete<ChatDebounceMarker>(Id);
            messages.Add(new ChatWindowExpired(message.SagaId, StartedAt.UtcTicks));
        }
        else if (StartedAt == default)
        {
            StartedAt = DateTimeOffset.UtcNow - StuckAfter - TimeSpan.FromSeconds(1);
        }

        await ChatDebounceRecovery.RepublishDueAsync(session, messages, this, ct);
        return messages;
    }

    internal bool WindowIsOverdue() =>
        StartedAt == default || StartedAt <= DateTimeOffset.UtcNow - StuckAfter;
}
