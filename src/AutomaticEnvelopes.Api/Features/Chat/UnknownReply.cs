namespace AutomaticEnvelopes.Api.Features.Chat;

public static class UnknownReply
{
    public const string Marker = "[[UNKNOWN]]";

    public const string Fallback =
        "I am sorry, I do not have that specific information at this moment. Please contact the organization directly.";

    /// <summary>
    /// The model ends an unanswered reply with <see cref="Marker"/>. That line is not shown to the user.
    /// </summary>
    public static bool TryStrip(string reply, out string visible)
    {
        var trimmed = reply.Trim();
        if (!trimmed.EndsWith(Marker, StringComparison.Ordinal))
        {
            visible = trimmed;
            return false;
        }

        visible = trimmed[..^Marker.Length].Trim();
        return true;
    }
}
