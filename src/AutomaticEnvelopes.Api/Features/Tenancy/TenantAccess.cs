using System.Security.Claims;
using System.Text.RegularExpressions;
using AutomaticEnvelopes.Api.Features.AdminAuth;

namespace AutomaticEnvelopes.Api.Features.Tenancy;

public static partial class TenantIds
{
    public const int MaxLength = 64;

    public static bool IsValid(string? tenantId) =>
        !string.IsNullOrEmpty(tenantId) &&
        tenantId.Length <= MaxLength &&
        IdPattern().IsMatch(tenantId);

    [GeneratedRegex("^[a-z0-9-]+$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex IdPattern();
}

public static class TenantAccess
{
    public const string AdminGroup = "admin";

    public static bool IsAuthenticated(ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated == true;

    public static bool IsAdmin(ClaimsPrincipal user) =>
        Groups(user).Contains(AdminGroup, StringComparer.Ordinal);

    public static bool CanAccess(ClaimsPrincipal user, string tenantId) =>
        IsAdmin(user) || Groups(user).Contains(tenantId, StringComparer.Ordinal);

    public static IReadOnlyList<string> VisibleTenantIds(ClaimsPrincipal user) =>
        Groups(user).Where(TenantIds.IsValid).Where(group => group != AdminGroup).Distinct(StringComparer.Ordinal).ToArray();

    public static IReadOnlyList<string> Groups(ClaimsPrincipal user)
    {
        var groups = new List<string>();
        groups.AddRange(CognitoGroupClaims.Read(user.Claims));
        groups.AddRange(user.FindAll(ClaimTypes.Role)
            .Where(role => !string.IsNullOrWhiteSpace(role.Value))
            .Select(role => role.Value.Trim()));

        return groups.Distinct(StringComparer.Ordinal).ToArray();
    }
}
