using AutomaticEnvelopes.Api.Features.Tenants.Sama.CampusPricing;

namespace AutomaticEnvelopes.Api.Features.Chat.Tools;

/// <summary>
/// Picks the tools a tenant may call. Null, empty, or blank config enables nothing:
/// the only registered tool is opt-in business logic, and retrieval is not a tool.
/// </summary>
public static class TenantToolGate
{
    public static IReadOnlyList<string> KnownNames { get; } = [CampusCalculatorTool.ToolName];

    public static IReadOnlyList<IBedrockTool> Select(IEnumerable<IBedrockTool> registered, IReadOnlyCollection<string>? enabledTools)
    {
        ArgumentNullException.ThrowIfNull(registered);
        if (enabledTools is null || enabledTools.Count == 0)
            return [];

        var allowed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in enabledTools)
        {
            if (!string.IsNullOrWhiteSpace(name))
                allowed.Add(name.Trim());
        }

        if (allowed.Count == 0)
            return [];

        return registered.Where(tool => allowed.Contains(tool.Name)).ToArray();
    }
}
