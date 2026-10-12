using AutomaticEnvelopes.Api.Core.Events;
using JasperFx.Events;
using Marten;
using Marten.Events;

namespace AutomaticEnvelopes.Api.Features.Portal;

public static class PortalConversations
{
    public static async Task<IReadOnlyList<PortalConversation>> ReadAsync(
        IDocumentStore store,
        string tenantId,
        CancellationToken ct)
    {
        using var session = store.LightweightSession(tenantId);
        var states = await session.Query<StreamState>().ToListAsync(ct);
        var list = new List<PortalConversation>();
        foreach (var state in states)
        {
            if (string.IsNullOrEmpty(state.Key))
            {
                continue;
            }

            var events = await session.Events.FetchStreamAsync(state.Key, token: ct);
            var mapped = MapStream(tenantId, state.Key, events);
            if (mapped is not null)
            {
                list.Add(mapped);
            }
        }

        var archived = await session.Query<AnonymizedChat>().ToListAsync(ct);
        list.AddRange(archived.Select(chat => MapArchived(tenantId, chat)));
        return list;
    }

    public static PortalConversation? Find(IReadOnlyList<PortalConversation> conversations, string conversationId) =>
        conversations.FirstOrDefault(conversation => conversation.Id == conversationId);

    public static IReadOnlyList<PortalConversation> Filter(
        IReadOnlyList<PortalConversation> conversations,
        string? q,
        string? language,
        string? from,
        string? to,
        bool includeAnonymized)
    {
        var query = q?.Trim();
        return conversations
            .Where(conversation => includeAnonymized || !conversation.Anonymized)
            .Where(conversation => language is null or "" or "all" || conversation.Language == language)
            .Where(conversation => InRange(Activity(conversation), from, to))
            .Where(conversation => string.IsNullOrEmpty(query) || Haystack(conversation).Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(Activity)
            .ToArray();
    }

    public static string Topic(string text)
    {
        var collapsed = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length <= 80 ? collapsed : collapsed[..80];
    }

    public static DateTimeOffset? Activity(PortalConversation conversation)
    {
        if (conversation.Events.Count > 0)
        {
            return conversation.Events[^1].At;
        }

        return conversation.DeletionRequestedAt;
    }

    private static PortalConversation? MapStream(string tenantId, string streamKey, IReadOnlyList<IEvent> events)
    {
        var mapped = new List<PortalConversationEvent>();
        foreach (var evt in events)
        {
            switch (evt.Data)
            {
                case MessageReceived message:
                    mapped.Add(new PortalConversationEvent
                    {
                        Type = "MessageReceived",
                        Text = message.Text,
                        At = message.ReceivedAt
                    });
                    break;
                case ReplyGenerated reply:
                    mapped.Add(new PortalConversationEvent
                    {
                        Type = "ReplyGenerated",
                        Text = reply.Text,
                        At = evt.Timestamp
                    });
                    break;
            }
        }

        if (mapped.Count == 0)
        {
            return null;
        }

        var opening = mapped.FirstOrDefault(item => item.Type == "MessageReceived")?.Text ?? string.Empty;
        return new PortalConversation
        {
            Id = PortalIds.ForStream(streamKey),
            TenantId = tenantId,
            UserPhone = streamKey,
            Topic = Topic(opening),
            Events = mapped
        };
    }

    private static PortalConversation MapArchived(string tenantId, AnonymizedChat chat)
    {
        var events = string.IsNullOrWhiteSpace(chat.Transcript)
            ? Array.Empty<PortalConversationEvent>()
            :
            [
                new PortalConversationEvent
                {
                    Type = "ReplyGenerated",
                    Text = chat.Transcript,
                    At = chat.ArchivedAt
                }
            ];

        return new PortalConversation
        {
            Id = chat.Id.ToString("D"),
            TenantId = tenantId,
            Topic = string.Empty,
            Anonymized = true,
            DeletionRequestedAt = chat.ArchivedAt,
            Events = events
        };
    }

    private static bool InRange(DateTimeOffset? at, string? from, string? to)
    {
        if (at is null)
        {
            return string.IsNullOrEmpty(from) && string.IsNullOrEmpty(to);
        }

        var day = PortalClock.Day(at.Value);
        if (PortalClock.IsDay(from) && string.CompareOrdinal(day, from) < 0)
        {
            return false;
        }

        if (PortalClock.IsDay(to) && string.CompareOrdinal(day, to) > 0)
        {
            return false;
        }

        return true;
    }

    private static string Haystack(PortalConversation conversation)
    {
        var phone = conversation.Anonymized
            ? "anonimizado borrado gdpr número eliminado"
            : $"{conversation.UserPhone} {new string(conversation.UserPhone.Where(char.IsDigit).ToArray())}";
        return string.Join(' ', new[] { conversation.Topic, conversation.Language, phone }.Concat(conversation.Events.Select(evt => evt.Text)));
    }
}
