namespace AutomaticEnvelopes.Api.Features.Chat.Tools;

public static class TenantToolGate
{
    public static IReadOnlyList<string> KnownNames { get; } = [];

    public static IReadOnlyList<IBedrockTool> Select(
        IEnumerable<IBedrockTool> registered,
        IReadOnlyCollection<string>? enabledTools,
        IReadOnlyCollection<string> knownNames)
    {
        ArgumentNullException.ThrowIfNull(registered);
        if (enabledTools is null || enabledTools.Count == 0 || knownNames.Count == 0)
            return [];

        var allowed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in enabledTools)
        {
            var trimmed = name?.Trim();
            if (!string.IsNullOrEmpty(trimmed) && knownNames.Contains(trimmed, StringComparer.Ordinal))
                allowed.Add(trimmed);
        }

        if (allowed.Count == 0)
            return [];

        return registered.Where(tool => allowed.Contains(tool.Name)).ToArray();
    }
}
