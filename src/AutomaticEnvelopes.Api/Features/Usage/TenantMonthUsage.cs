using Marten.Schema;

namespace AutomaticEnvelopes.Api.Features.Usage;

/// <summary>
/// One Marten document per tenant and calendar month. The id is the month (yyyy-MM)
/// inside the tenant session. Clubs operate in Spain, so the month is Europe/Madrid
/// (CET/CEST), not UTC: the quota resets at local midnight.
/// </summary>
[UseOptimisticConcurrency]
public class TenantMonthUsage
{
    public string Id { get; set; } = string.Empty;

    public int Responses { get; set; }

    public long InputTokens { get; set; }

    public long OutputTokens { get; set; }

    public bool Notified80 { get; set; }

    public bool Notified100 { get; set; }

    public Guid Version { get; set; }
}

public static class UsageMonth
{
    public const string TimeZoneId = "Europe/Madrid";

    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);

    public static string Key(DateTimeOffset utc) =>
        TimeZoneInfo.ConvertTime(utc, Zone).ToString("yyyy-MM");

    public static string Current() => Key(DateTimeOffset.UtcNow);
}

public enum UsageAlert
{
    None = 0,
    Warning = 80,
    Reached = 100
}

public static class UsageMath
{
    public static int Threshold(int limit, int percent)
    {
        if (limit <= 0)
            return percent >= 100 ? 0 : int.MaxValue;

        return (int)((percent * (long)limit + 99) / 100);
    }

    public static UsageAlert Pending(TenantMonthUsage doc, int limit)
    {
        if (!doc.Notified100 && doc.Responses >= Threshold(limit, 100))
            return UsageAlert.Reached;

        if (!doc.Notified80 && doc.Responses >= Threshold(limit, 80))
            return UsageAlert.Warning;

        return UsageAlert.None;
    }
}

public static class UsageMessages
{
    public const string Spanish = "Este servicio no está disponible este mes. Contacta con el club.";
}
