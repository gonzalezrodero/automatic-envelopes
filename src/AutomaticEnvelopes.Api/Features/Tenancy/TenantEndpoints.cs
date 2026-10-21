using System.Security.Claims;
using AutomaticEnvelopes.Api.Features.AdminAuth;
using AutomaticEnvelopes.Api.Features.Chat.Tools;
using AutomaticEnvelopes.Api.Features.Usage;
using Marten;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Wolverine.Http;

namespace AutomaticEnvelopes.Api.Features.Tenancy;

public class TenantEndpoints
{
    protected TenantEndpoints()
    {
    }

    [Authorize]
    [WolverineGet("/tenants")]
    [EnableRateLimiting(AdminAuthPolicies.Auth)]
    public static async Task<IResult> List(
        ClaimsPrincipal user,
        HttpContext httpContext,
        IDocumentSession session,
        CancellationToken ct)
    {
        TenantRequest.NoStore(httpContext);
        if (Unauthenticated(user) is { } rejected)
        {
            return rejected;
        }

        var profiles = await LoadVisibleAsync(user, session, ct);
        var response = profiles
            .Select(TenantPortalProfile.From)
            .OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(profile => profile.Id, StringComparer.Ordinal)
            .ToArray();
        return Results.Ok(response);
    }

    [Authorize(Policy = "TenantAdmin")]
    [WolverineGet("/tenants/{tenantId}")]
    [EnableRateLimiting(AdminAuthPolicies.Auth)]
    public static async Task<IResult> Get(
        string tenantId,
        ClaimsPrincipal user,
        HttpContext httpContext,
        IDocumentSession session,
        CancellationToken ct)
    {
        TenantRequest.NoStore(httpContext);
        if (TenantRequest.Reject(user, tenantId) is { } rejected)
        {
            return rejected;
        }

        var profile = await session.LoadAsync<TenantProfile>(tenantId, ct);
        return profile is null
            ? Results.NotFound(new { Error = "Tenant was not found." })
            : Results.Ok(TenantPortalProfile.From(profile));
    }

    [Authorize(Policy = "TenantAdmin")]
    [WolverinePatch("/tenants/{tenantId}")]
    [EnableRateLimiting(AdminAuthPolicies.Auth)]
    public static async Task<IResult> Update(
        string tenantId,
        UpdateTenantSettingsRequest? request,
        ClaimsPrincipal user,
        HttpContext httpContext,
        IDocumentSession session,
        CancellationToken ct)
    {
        TenantRequest.NoStore(httpContext);
        if (TenantRequest.Reject(user, tenantId) is { } rejected)
        {
            return rejected;
        }

        if (RejectClubOnlyFields(request, user) is { } forbidden)
        {
            return forbidden;
        }

        if (!TenantSettingsValidation.TryValidate(request, out var settings, out var error))
        {
            return Results.BadRequest(new { Error = error });
        }

        if (!TenantSettingsValidation.TryReadEnabledTools(
                request.EnabledTools,
                TenantToolGate.KnownNames,
                out var enabledTools,
                out error))
        {
            return Results.BadRequest(new { Error = error });
        }

        if (!TenantSettingsValidation.TryReadMonthlyResponseLimit(request.MonthlyResponseLimit, out var limit, out error) ||
            !TenantSettingsValidation.TryReadContactEmail(request.ContactEmail, out var email, out error))
        {
            return Results.BadRequest(new { Error = error });
        }

        var profile = await session.LoadAsync<TenantProfile>(tenantId, ct);
        if (profile is null)
        {
            return Results.NotFound(new { Error = "Tenant was not found." });
        }

        var updated = profile.WithSettings(
            settings.SystemPrompt,
            settings.PrivacyPolicyUrl,
            enabledTools ?? profile.EnabledTools,
            limit ?? profile.MonthlyResponseLimit,
            email ?? profile.ContactEmail);
        session.Store(updated);
        await session.SaveChangesAsync(ct);
        return Results.Ok(TenantPortalProfile.From(updated));
    }

    [Authorize(Policy = "TenantAdmin")]
    [WolverineGet("/tenants/{tenantId}/usage")]
    [EnableRateLimiting(AdminAuthPolicies.Auth)]
    public static async Task<IResult> Usage(
        string tenantId,
        ClaimsPrincipal user,
        HttpContext httpContext,
        IDocumentSession session,
        IDocumentStore store,
        CancellationToken ct)
    {
        TenantRequest.NoStore(httpContext);
        if (TenantRequest.Reject(user, tenantId) is { } rejected)
        {
            return rejected;
        }

        var profile = await session.LoadAsync<TenantProfile>(tenantId, ct);
        if (profile is null)
        {
            return Results.NotFound(new { Error = "Tenant was not found." });
        }

        var month = UsageMonth.Current();
        await using var usageSession = store.LightweightSession(tenantId);
        var usage = await usageSession.LoadAsync<TenantMonthUsage>(month, ct);
        return Results.Ok(TenantUsageResponse.Create(profile, usage, month));
    }

    private static IResult? RejectClubOnlyFields(UpdateTenantSettingsRequest? request, ClaimsPrincipal user)
    {
        if (TenantAccess.IsAdmin(user) || request is null)
        {
            return null;
        }

        if (request.EnabledTools is not null)
        {
            return Forbidden("Only a platform admin can set enabledTools.");
        }

        if (request.MonthlyResponseLimit is not null)
        {
            return Forbidden("Only a platform admin can set monthlyResponseLimit.");
        }

        if (request.ContactEmail is not null)
        {
            return Forbidden("Only a platform admin can set contactEmail.");
        }

        return null;
    }

    private static IResult Forbidden(string error) =>
        Results.Json(new { Error = error }, statusCode: StatusCodes.Status403Forbidden);

    private static async Task<IReadOnlyList<TenantProfile>> LoadVisibleAsync(
        ClaimsPrincipal user,
        IDocumentSession session,
        CancellationToken ct)
    {
        if (TenantAccess.IsAdmin(user))
        {
            return await session.Query<TenantProfile>().ToListAsync(ct);
        }

        var allowed = TenantAccess.VisibleTenantIds(user);
        if (allowed.Count == 0)
        {
            return [];
        }

        var ids = allowed.ToArray();
        return await session.Query<TenantProfile>()
            .Where(profile => profile.Id.IsOneOf(ids))
            .ToListAsync(ct);
    }

    private static IResult? Unauthenticated(ClaimsPrincipal user) =>
        TenantAccess.IsAuthenticated(user) ? null : Results.Unauthorized();
}
