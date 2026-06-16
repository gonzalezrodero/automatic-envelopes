using AutomaticEnvelopes.Api.Core.Events;
using Marten;
using Wolverine;

namespace AutomaticEnvelopes.Api.Features.Chat;

/// <summary>
/// Background worker that executes the GDPR hard delete and AI anonymization.
/// Triggered by the DeleteChatHistoryCommand.
/// </summary>
public static class DeleteChatHistoryHandler
{
    public static async Task Handle(
        DeleteChatHistoryCommand command,
        IDocumentStore store,
        IChatService chatService,
        IMessageBus bus,
        CancellationToken ct)
    {
        using var session = store.LightweightSession(command.TenantId);

        var streamEvents = await session.Events.FetchStreamAsync(command.PhoneNumber, token: ct);
        if (streamEvents.Count == 0) return;

        var rawTranscript = string.Join("\n", streamEvents.Select(e => e.Data switch
        {
            MessageReceived m => $"User: {m.Text}",
            ReplyGenerated r => $"Bot: {r.Text}",
            _ => ""
        }).Where(t => !string.IsNullOrWhiteSpace(t)));

        // 3. AI Data Masking (Takes ~2-4 seconds via Bedrock)
        var sanitizedTranscript = await chatService.SanitizeHistoryAsync(rawTranscript, ct);

        // 4. Save the anonymized data as a standalone document
        var deletedId = Guid.NewGuid();
        var anonymizedChat = new AnonymizedChat(deletedId, DateTimeOffset.UtcNow, sanitizedTranscript);
        session.Store(anonymizedChat);

        // 5. The Physical Deletion 
        session.QueueSqlCommand("DELETE FROM mt_events WHERE stream_id = ? AND tenant_id = ?", command.PhoneNumber, command.TenantId);
        session.QueueSqlCommand("DELETE FROM mt_streams WHERE id = ? AND tenant_id = ?", command.PhoneNumber, command.TenantId);
        await session.SaveChangesAsync(ct);

        // 6. Multi-language Confirmation Message
        var finalConfirmation = new ReplyGenerated(
            MessageId: command.MessageId,
            BotPhoneNumberId: command.BotPhoneNumberId,
            PhoneNumber: command.PhoneNumber,
            Text: BotPrompts.DeleteDataSuccessReply,
            TenantId: command.TenantId);

        await bus.InvokeAsync(finalConfirmation, ct);
    }
}