using System.Data;
using AutomaticEnvelopes.Api.Common;
using JasperFx;
using Marten;
using Marten.Linq.LastModified;
using Wolverine;

namespace AutomaticEnvelopes.Api.Features.Chat;

/// <summary>
/// One row per closed debounce window (id = saga id) and one lease row that keeps a
/// single recovery chain alive. A duplicate expiry for that same window sees the row
/// and does not send a second reply. The next window deletes it.
/// </summary>
public sealed class ChatDebounceMarker
{
    public const string RecoveryId = "recovery";

    public string Id { get; set; } = string.Empty;

    public DateTimeOffset NotBefore { get; set; }
}

public record RecoverStuckChatDebounceSagas
{
    public const string Kind = "recover-stuck-chat-sagas";

    public string MessageType { get; init; } = Kind;
}

public static class ChatDebounceRecovery
{
    private static readonly TimeSpan DueGrace = TimeSpan.FromSeconds(30);

    public static async Task<bool> TryStartChainAsync(IDocumentStore store, CancellationToken ct)
    {
        using var session = store.LightweightSession(IsolationLevel.ReadCommitted);
        var now = DateTimeOffset.UtcNow;
        var lease = await session.LoadAsync<ChatDebounceMarker>(ChatDebounceMarker.RecoveryId, ct);
        if (lease != null && lease.NotBefore > now.Add(DueGrace))
        {
            return false;
        }

        var notBefore = now.Add(ChatDebounceSaga.StuckAfter);
        if (lease == null)
        {
            session.Insert(new ChatDebounceMarker { Id = ChatDebounceMarker.RecoveryId, NotBefore = notBefore });
        }
        else
        {
            lease.NotBefore = notBefore;
            session.Store(lease);
        }

        try
        {
            await session.SaveChangesAsync(ct);
            return true;
        }
        catch (Exception ex) when (ex is ConcurrencyException or DocumentAlreadyExistsException
            || ex.InnerException is ConcurrencyException or DocumentAlreadyExistsException)
        {
            return false;
        }
    }
}

public static class ChatWindowExpiredHandler
{
    public static async Task<OutgoingMessages> Handle(
        ChatWindowExpired expired,
        IDocumentSession session,
        ILogger<ChatWindowExpired> logger,
        CancellationToken ct)
    {
        var messages = new OutgoingMessages();
        var saga = await session.LoadAsync<ChatDebounceSaga>(expired.SagaId, ct);
        if (saga == null)
        {
            logger.LogInformation("Debounce window for {SagaId} was already closed.", LogMask.Id(expired.SagaId));
            return messages;
        }

        // A late expiry from the previous window must not close, or overwrite, the one open now.
        if (expired.WindowStartedAtTicks != saga.StartedAt.UtcTicks)
        {
            logger.LogInformation(
                "Ignoring debounce expiry for {SagaId} because it belongs to an older window.",
                LogMask.Id(expired.SagaId));
            return messages;
        }

        // Same id as the saga. The loser of a duplicate expiry finds the row and finishes
        // without a reply. The next window deletes it.
        if (await session.LoadAsync<ChatDebounceMarker>(saga.Id, ct) != null)
        {
            logger.LogInformation("Debounce window for {PhoneNumber} was already closed.", LogMask.Phone(saga.PhoneNumber));
            session.Delete(saga);
            return messages;
        }

        session.Insert(new ChatDebounceMarker { Id = saga.Id });
        logger.LogInformation("Debounce window closed for {PhoneNumber}. Dispatching to AI.", LogMask.Phone(saga.PhoneNumber));
        session.Delete(saga);
        messages.Add(new AnalyzeChatSession(saga.PhoneNumber, saga.TenantId, saga.BotPhoneNumberId, saga.CombinedText));
        return messages;
    }
}

public static class RecoverStuckChatDebounceSagasHandler
{
    public static async Task<OutgoingMessages> Handle(
        RecoverStuckChatDebounceSagas _,
        IDocumentSession session,
        IDocumentStore store,
        ILogger logger,
        CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow - ChatDebounceSaga.StuckAfter;
        var epoch = DateTimeOffset.UnixEpoch;
        var stuck = await session.Query<ChatDebounceSaga>()
            .Where(saga => (saga.StartedAt > epoch && saga.StartedAt < cutoff) || saga.ModifiedBefore(cutoff))
            .ToListAsync(ct);

        var messages = new OutgoingMessages();
        foreach (var saga in stuck)
        {
            messages.Add(new ChatWindowExpired(saga.Id, saga.StartedAt.UtcTicks));
        }

        if (stuck.Count > 0)
        {
            logger.LogInformation("Re-publishing debounce expiry for {Count} stuck chat sagas.", stuck.Count);
        }

        if (await ChatDebounceRecovery.TryStartChainAsync(store, ct))
        {
            messages.Add(new RecoverStuckChatDebounceSagas().DelayedFor(ChatDebounceSaga.StuckAfter));
        }

        return messages;
    }
}
