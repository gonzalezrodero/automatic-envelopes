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

public readonly record struct UsageUpdate(bool Save, UsageAlert Alert, int Responses);

public static class UsageMath
{
    public static int Threshold(int limit, int percent)
    {
        if (limit <= 0)
        {
            return percent >= 100 ? 0 : int.MaxValue;
        }

        return (int)((percent * (long)limit + 99) / 100);
    }

    public static UsageUpdate Record(TenantMonthUsage doc, long inputTokens, long outputTokens, int limit)
    {
        doc.Responses++;
        doc.InputTokens += inputTokens;
        doc.OutputTokens += outputTokens;

        if (!doc.Notified100 && doc.Responses >= Threshold(limit, 100))
        {
            doc.Notified100 = true;
            doc.Notified80 = true;
            return new UsageUpdate(true, UsageAlert.Reached, doc.Responses);
        }

        if (!doc.Notified80 && doc.Responses >= Threshold(limit, 80))
        {
            doc.Notified80 = true;
            return new UsageUpdate(true, UsageAlert.Warning, doc.Responses);
        }

        return new UsageUpdate(true, UsageAlert.None, doc.Responses);
    }

    public static UsageUpdate MarkBlocked(TenantMonthUsage doc, int limit)
    {
        if (doc.Responses < limit || doc.Notified100)
        {
            return new UsageUpdate(false, UsageAlert.None, doc.Responses);
        }

        doc.Notified100 = true;
        doc.Notified80 = true;
        return new UsageUpdate(true, UsageAlert.Reached, doc.Responses);
    }
}

public static class UsageMessages
{
    public const string Spanish = "Este servicio no está disponible este mes. Contacta con el club.";
    public const string Catalan = "Aquest servei no està disponible aquest mes. Contacta amb el club.";
    public const string English = "This service is unavailable this month. Please contact the club.";

    private static readonly string[] CatalanTokens = ["quina", "què", "gràcies"];
    private static readonly string[] EnglishTokens = ["hello", "please", "what", "when", "where", "thanks"];

    public static string LimitReached(string? userText)
    {
        if (Contains(userText, "si us plau") || HasToken(userText, CatalanTokens))
        {
            return Catalan;
        }

        if (HasToken(userText, EnglishTokens))
        {
            return English;
        }

        return Spanish;
    }

    private static bool HasToken(string? text, string[] tokens)
    {
        foreach (var token in tokens)
        {
            if (HasWord(text, token))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Contains(string? text, string phrase) =>
        !string.IsNullOrEmpty(text) && text.Contains(phrase, StringComparison.OrdinalIgnoreCase);

    private static bool HasWord(string? text, string word)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var index = 0;
        while ((index = text.IndexOf(word, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var before = index == 0 || !char.IsLetter(text[index - 1]);
            var end = index + word.Length;
            var after = end >= text.Length || !char.IsLetter(text[end]);
            if (before && after)
            {
                return true;
            }

            index = end;
        }

        return false;
    }
}

public static class UsageMail
{
    public static string Subject(UsageAlert alert) =>
        alert == UsageAlert.Reached
            ? "Límite de respuestas alcanzado"
            : "Aviso de uso del asistente (80%)";

    public static string Body(string name, string month, int responses, int limit, UsageAlert alert)
    {
        if (alert == UsageAlert.Reached)
        {
            return $"{name} ha alcanzado el límite de {limit} respuestas de {month}. El asistente no responderá preguntas nuevas hasta el mes que viene. Para ampliar el límite, contacta con el administrador de la plataforma.";
        }

        return $"{name} lleva {responses} de {limit} respuestas de {month}. Al llegar al límite el asistente dejará de responder hasta el mes que viene.";
    }
}
