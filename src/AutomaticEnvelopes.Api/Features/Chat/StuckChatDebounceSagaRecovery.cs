using JasperFx;
using Marten;
using Marten.Linq.LastModified;
using Wolverine;

namespace AutomaticEnvelopes.Api.Features.Chat;

/// <summary>
/// One row per closed debounce window (id = saga id) and one lease row. The lease only
/// decides when the next user message may republish overdue expiries. It is written in
/// that same session, so a rolled-back message does not consume the lease.
/// </summary>
public sealed class ChatDebounceMarker
{
    public const string RecoveryId = "recovery";

    public string Id { get; set; } = string.Empty;

    public DateTimeOffset NotBefore { get; set; }

    /// <summary>Saga revision this window was answered at. A newer revision is answered again.</summary>
    public int ClaimedVersion { get; set; }
}

public static class ChatDebounceRecovery
{
    public static async Task RepublishDueAsync(
        IDocumentSession session,
        OutgoingMessages messages,
        ChatDebounceSaga current,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var lease = await session.LoadAsync<ChatDebounceMarker>(ChatDebounceMarker.RecoveryId, ct);
        if (lease != null && lease.NotBefore > now)
        {
            return;
        }

        if (current.WindowIsOverdue())
        {
            messages.Add(new ChatWindowExpired(current.Id, current.StartedAt.UtcTicks));
        }

        var cutoff = now - ChatDebounceSaga.StuckAfter;
        var epoch = DateTimeOffset.UnixEpoch;
        var stuck = await session.Query<ChatDebounceSaga>()
            .Where(saga => (saga.StartedAt > epoch && saga.StartedAt < cutoff) || saga.ModifiedBefore(cutoff))
            .ToListAsync(ct);

        foreach (var saga in stuck)
        {
            if (saga.Id == current.Id)
            {
                continue;
            }

            messages.Add(new ChatWindowExpired(saga.Id, saga.StartedAt.UtcTicks));
        }

        session.Store(new ChatDebounceMarker
        {
            Id = ChatDebounceMarker.RecoveryId,
            NotBefore = now.Add(ChatDebounceSaga.StuckAfter)
        });
    }
}

public static class ChatWindowExpiredHandler
{
    private const string LockSql =
        "select mt_version from public.mt_doc_chatdebouncesaga where id = ? for update";

    private const string DeleteSql =
        "delete from public.mt_doc_chatdebouncesaga where id = ? and mt_version = ? returning id";

    public static async Task Handle(
        ChatWindowExpired expired,
        IDocumentSession session,
        IMessageBus bus,
        CancellationToken ct)
    {
        await session.BeginTransactionAsync(ct);
        var locked = await session.AdvancedSql.QueryAsync<long>(LockSql, ct, expired.SagaId);
        if (locked.Count == 0)
        {
            return;
        }

        var saga = await session.LoadAsync<ChatDebounceSaga>(expired.SagaId, ct);
        if (saga == null || saga.StartedAt.UtcTicks != expired.WindowStartedAtTicks)
        {
            return;
        }

        var marker = await session.LoadAsync<ChatDebounceMarker>(saga.Id, ct);
        if (marker != null && marker.ClaimedVersion == saga.Version)
        {
            await DeleteSagaAsync(session, saga, locked[0], ct);
            return;
        }

        if (marker != null)
        {
            session.Delete(marker);
        }

        // The reply stays inside this SQS attempt. The claim commits only after it succeeds.
        await bus.InvokeAsync(
            new AnalyzeChatSession(saga.PhoneNumber, saga.TenantId, saga.BotPhoneNumberId, saga.CombinedText),
            ct);

        await DeleteSagaAsync(session, saga, locked[0], ct);
        session.Insert(new ChatDebounceMarker { Id = saga.Id, ClaimedVersion = saga.Version });
    }

    private static async Task DeleteSagaAsync(
        IDocumentSession session,
        ChatDebounceSaga saga,
        long version,
        CancellationToken ct)
    {
        var deleted = await session.AdvancedSql.QueryAsync<string>(DeleteSql, ct, saga.Id, version);
        if (deleted.Count == 0)
        {
            throw new ConcurrencyException($"Chat debounce saga '{saga.Id}' changed before it was closed.");
        }

        session.Eject(saga);
    }
}
