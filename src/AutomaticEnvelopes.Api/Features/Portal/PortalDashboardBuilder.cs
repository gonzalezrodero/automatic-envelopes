namespace AutomaticEnvelopes.Api.Features.Portal;

public static class PortalDashboardBuilder
{
    private const int WindowDays = 7;
    private const int ChartDays = 14;

    public static PortalDashboard Build(IReadOnlyList<PortalConversation> conversations, DateTimeOffset now)
    {
        var today = DateOnly.Parse(PortalClock.Day(now));
        var windowStart = today.AddDays(-(WindowDays - 1));
        var chartStart = today.AddDays(-(ChartDays - 1));
        var activity = new Dictionary<string, PortalDayActivity>();
        for (var day = chartStart; day <= today; day = day.AddDays(1))
        {
            var key = day.ToString("yyyy-MM-dd");
            var noon = new DateTimeOffset(day.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero);
            activity[key] = new PortalDayActivity
            {
                Date = key,
                Label = PortalClock.Label(noon)
            };
        }

        var topics = new Dictionary<string, (int Count, string Latest)>(StringComparer.Ordinal);
        var languages = new Dictionary<string, int>(StringComparer.Ordinal);
        var activePhones = new HashSet<string>(StringComparer.Ordinal);
        var messagesThisWeek = 0;
        var deletionRequests = 0;

        foreach (var conversation in conversations)
        {
            if (conversation.Anonymized)
            {
                deletionRequests += 1;
                continue;
            }

            if (conversation.Language is "es" or "ca" or "en")
            {
                languages[conversation.Language] = languages.GetValueOrDefault(conversation.Language) + 1;
            }

            foreach (var evt in conversation.Events)
            {
                var key = PortalClock.Day(evt.At);
                if (activity.TryGetValue(key, out var bucket))
                {
                    if (evt.Type == "MessageReceived") bucket.Received += 1;
                    else bucket.Replied += 1;
                }

                if (evt.Type == "MessageReceived" && conversation.Topic.Length > 0)
                {
                    var at = evt.At.ToString("o");
                    var current = topics.GetValueOrDefault(conversation.Topic);
                    var latest = current.Latest is null || string.CompareOrdinal(at, current.Latest) > 0 ? at : current.Latest;
                    topics[conversation.Topic] = (current.Count + 1, latest);
                }

                if (string.CompareOrdinal(key, windowStart.ToString("yyyy-MM-dd")) >= 0 &&
                    string.CompareOrdinal(key, today.ToString("yyyy-MM-dd")) <= 0)
                {
                    messagesThisWeek += 1;
                    if (evt.Type == "MessageReceived" && conversation.UserPhone.Length > 0)
                    {
                        activePhones.Add(conversation.UserPhone);
                    }
                }
            }
        }

        var ranked = topics
            .OrderByDescending(item => item.Value.Count)
            .ThenByDescending(item => item.Value.Latest, StringComparer.Ordinal)
            .Select(item => new PortalTopicCount { Topic = item.Key, Count = item.Value.Count })
            .ToArray();

        var recent = conversations
            .OrderByDescending(PortalConversations.Activity)
            .Take(4)
            .Select(conversation =>
            {
                var at = PortalConversations.Activity(conversation);
                return new PortalConversationPreview
                {
                    Id = conversation.Id,
                    Topic = conversation.Topic,
                    Language = conversation.Language,
                    Preview = Preview(conversation),
                    At = at?.ToString("o") ?? string.Empty,
                    Anonymized = conversation.Anonymized,
                    PhoneMasked = conversation.Anonymized ? "Número eliminado" : PortalPhone.Mask(conversation.UserPhone)
                };
            })
            .ToArray();

        var windowLabel = $"{PortalClock.LongDay(new DateTimeOffset(windowStart.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero))} – {PortalClock.LongDay(new DateTimeOffset(today.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero))}";

        return new PortalDashboard
        {
            GeneratedAt = now,
            WindowLabel = windowLabel,
            ConversationCount = conversations.Count,
            MessagesThisWeek = messagesThisWeek,
            ActiveUsers = activePhones.Count,
            DeletionRequests = deletionRequests,
            TopTopic = ranked.FirstOrDefault(),
            Topics = ranked.Take(5).ToArray(),
            Languages = new[] { "es", "ca", "en" }
                .Where(languages.ContainsKey)
                .Select(language => new PortalLanguageCount { Language = language, Count = languages[language] })
                .ToArray(),
            Activity = activity.Values.ToArray(),
            Recent = recent
        };
    }

    private static string Preview(PortalConversation conversation)
    {
        if (conversation.Anonymized)
        {
            return "Historial anonimizado tras una solicitud de borrado.";
        }

        var last = conversation.Events.LastOrDefault(evt => !string.IsNullOrWhiteSpace(evt.Text));
        if (last is null)
        {
            return "Sin mensajes";
        }

        return last.Type == "ReplyGenerated" ? $"Bot · {last.Text}" : last.Text;
    }
}
