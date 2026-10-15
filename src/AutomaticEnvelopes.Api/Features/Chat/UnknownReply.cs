namespace AutomaticEnvelopes.Api.Features.Chat;

public static class UnknownReply
{
    public const string Marker = "[[UNKNOWN]]";

    public const string Fallback =
        "I am sorry, I do not have that specific information at this moment. Please contact the organization directly.";

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
