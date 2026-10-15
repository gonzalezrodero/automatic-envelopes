using System.Globalization;

namespace AutomaticEnvelopes.Api.Features.Portal;

public static class PortalDashboardBuilder
{
    private const int WindowDays = 7;
    private const int ChartDays = 14;
    private const string DayFormat = "yyyy-MM-dd";

    public static PortalDashboard Build(IReadOnlyList<PortalConversation> conversations, DateTimeOffset now)
    {
        var today = DateOnly.ParseExact(PortalClock.Day(now), DayFormat, CultureInfo.InvariantCulture);
        var windowStart = today.AddDays(-(WindowDays - 1));
        var tally = new DashboardTally
        {
            Activity = Chart(today.AddDays(-(ChartDays - 1)), today),
            WindowStart = windowStart,
            Today = today
        };

        foreach (var conversation in conversations)
        {
            CountConversation(conversation, tally);
        }

        var ranked = Rank(tally.Topics);
        return new PortalDashboard
        {
            GeneratedAt = now,
            WindowLabel = WindowLabel(windowStart, today),
            ConversationCount = conversations.Count,
            MessagesThisWeek = tally.MessagesThisWeek,
            ActiveUsers = tally.ActivePhones.Count,
            DeletionRequests = tally.DeletionRequests,
            TopTopic = ranked.FirstOrDefault(),
            Topics = ranked.Take(5).ToArray(),
            Activity = tally.Activity.Values.ToArray(),
            Recent = Recent(conversations)
        };
    }

    private static Dictionary<string, PortalDayActivity> Chart(DateOnly chartStart, DateOnly today)
    {
        var activity = new Dictionary<string, PortalDayActivity>();
        for (var day = chartStart; day <= today; day = day.AddDays(1))
        {
            var key = day.ToString(DayFormat, CultureInfo.InvariantCulture);
            var noon = new DateTimeOffset(day.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero);
            activity[key] = new PortalDayActivity
            {
                Date = key,
                Label = PortalClock.Label(noon)
            };
        }

        return activity;
    }

    private static void CountConversation(PortalConversation conversation, DashboardTally tally)
    {
        if (conversation.Anonymized)
        {
            tally.DeletionRequests += 1;
            return;
        }

        foreach (var evt in conversation.Events)
        {
            CountEvent(conversation, evt, tally);
        }
    }

    private static void CountEvent(PortalConversation conversation, PortalConversationEvent evt, DashboardTally tally)
    {
        var key = PortalClock.Day(evt.At);
        if (tally.Activity.TryGetValue(key, out var bucket))
        {
            if (evt.Type == "MessageReceived") bucket.Received += 1;
            else bucket.Replied += 1;
        }

        RememberTopic(conversation, evt, tally.Topics);

        if (!InWindow(key, tally.WindowStart, tally.Today))
        {
            return;
        }

        tally.MessagesThisWeek += 1;
        if (evt.Type == "MessageReceived" && conversation.UserPhone.Length > 0)
        {
            tally.ActivePhones.Add(conversation.UserPhone);
        }
    }

    private static void RememberTopic(
        PortalConversation conversation,
        PortalConversationEvent evt,
        Dictionary<string, (int Count, string Latest)> topics)
    {
        if (evt.Type != "MessageReceived" || conversation.Topic.Length == 0)
        {
            return;
        }

        var at = evt.At.ToString("o", CultureInfo.InvariantCulture);
        var current = topics.GetValueOrDefault(conversation.Topic);
        var latest = current.Latest is null || string.CompareOrdinal(at, current.Latest) > 0 ? at : current.Latest;
        topics[conversation.Topic] = (current.Count + 1, latest);
    }

    private static bool InWindow(string key, DateOnly windowStart, DateOnly today) =>
        string.CompareOrdinal(key, windowStart.ToString(DayFormat, CultureInfo.InvariantCulture)) >= 0 &&
        string.CompareOrdinal(key, today.ToString(DayFormat, CultureInfo.InvariantCulture)) <= 0;

    private static PortalTopicCount[] Rank(Dictionary<string, (int Count, string Latest)> topics) =>
        topics
            .OrderByDescending(item => item.Value.Count)
            .ThenByDescending(item => item.Value.Latest, StringComparer.Ordinal)
            .Select(item => new PortalTopicCount { Topic = item.Key, Count = item.Value.Count })
            .ToArray();

    private static PortalConversationPreview[] Recent(IReadOnlyList<PortalConversation> conversations) =>
        conversations
            .OrderByDescending(PortalConversations.Activity)
            .Take(4)
            .Select(PreviewOf)
            .ToArray();

    private static PortalConversationPreview PreviewOf(PortalConversation conversation)
    {
        var at = PortalConversations.Activity(conversation);
        return new PortalConversationPreview
        {
            Id = conversation.Id,
            Topic = conversation.Topic,
            Preview = Preview(conversation),
            At = at?.ToString("o", CultureInfo.InvariantCulture) ?? string.Empty,
            Anonymized = conversation.Anonymized,
            PhoneMasked = conversation.Anonymized ? "Número eliminado" : PortalPhone.Mask(conversation.UserPhone)
        };
    }

    private static string WindowLabel(DateOnly windowStart, DateOnly today)
    {
        var start = PortalClock.LongDay(new DateTimeOffset(windowStart.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero));
        var end = PortalClock.LongDay(new DateTimeOffset(today.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero));
        return $"{start} – {end}";
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

    private sealed class DashboardTally
    {
        public required Dictionary<string, PortalDayActivity> Activity { get; init; }
        public Dictionary<string, (int Count, string Latest)> Topics { get; } = new(StringComparer.Ordinal);
        public HashSet<string> ActivePhones { get; } = new(StringComparer.Ordinal);
        public required DateOnly WindowStart { get; init; }
        public required DateOnly Today { get; init; }
        public int MessagesThisWeek { get; set; }
        public int DeletionRequests { get; set; }
    }
}
