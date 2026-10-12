namespace AutomaticEnvelopes.Api.Features.Portal;

public static class PortalClock
{
    private static readonly TimeZoneInfo Madrid = TimeZoneInfo.FindSystemTimeZoneById("Europe/Madrid");

    public static string Day(DateTimeOffset instant) =>
        TimeZoneInfo.ConvertTime(instant, Madrid).ToString("yyyy-MM-dd");

    public static bool IsDay(string? value) =>
        value is { Length: 10 } && DateOnly.TryParseExact(value, "yyyy-MM-dd", out _);

    public static string Label(DateTimeOffset instant) =>
        TimeZoneInfo.ConvertTime(instant, Madrid).ToString("d MMM", System.Globalization.CultureInfo.GetCultureInfo("es-ES")).Replace(".", "");

    public static string LongDay(DateTimeOffset instant) =>
        TimeZoneInfo.ConvertTime(instant, Madrid).ToString("d 'de' MMMM", System.Globalization.CultureInfo.GetCultureInfo("es-ES"));
}
