using Marten.Schema;

namespace AutomaticEnvelopes.Api.Features.Usage;

/// <summary>One Marten document per tenant and Europe/Madrid month. The quota resets at local midnight, including daylight saving.</summary>
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
    private static readonly string[] MonthNames =
    [
        "enero", "febrero", "marzo", "abril", "mayo", "junio",
        "julio", "agosto", "septiembre", "octubre", "noviembre", "diciembre"
    ];

    public static string Key(DateTimeOffset utc) => TimeZoneInfo.ConvertTime(utc, Zone).ToString("yyyy-MM");

    public static string Current() => Key(DateTimeOffset.UtcNow);

    public static string Spoken(DateTimeOffset utc)
    {
        var local = TimeZoneInfo.ConvertTime(utc, Zone);
        return $"{MonthNames[local.Month - 1]} de {local.Year}";
    }
}

public static class UsageMessages
{
    public const string Spanish = "Este mes ya no puedo contestarte. Si lo necesitas, contacta con el club.";
    public const string OrganizationFallback = "Tu organización";

    public static string EmailName(string? name)
    {
        var raw = string.IsNullOrWhiteSpace(name) ? OrganizationFallback : name.Trim();
        return raw.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
    }
}
