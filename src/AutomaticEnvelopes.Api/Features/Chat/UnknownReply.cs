namespace AutomaticEnvelopes.Api.Features.Chat;

public static class UnknownReply
{
    public const string Marker = "[[UNKNOWN]]";

    public const string Fallback =
        "No tengo ese dato. Pregúntaselo a tu club.";

    /// <summary>
    /// Removes <see cref="Marker"/> wherever the model placed it, including when the privacy notice follows it.
    /// </summary>
    public static bool TryStrip(string reply, out string visible)
    {
        var trimmed = reply.Trim();
        if (!trimmed.Contains(Marker, StringComparison.Ordinal))
        {
            visible = trimmed;
            return false;
        }

        var lines = trimmed
            .Replace(Marker, "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        visible = string.Join("\n", lines);
        return true;
    }
}
