using System.Security.Claims;
using AutomaticEnvelopes.Api.Features.AdminAuth;
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
        NoStore(httpContext);
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
        NoStore(httpContext);
        if (Gate(user, tenantId) is { } rejected)
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
        NoStore(httpContext);
        if (Gate(user, tenantId) is { } rejected)
        {
            return rejected;
        }

        if (!TenantSettingsValidation.TryValidate(request, out var settings, out var error))
        {
            return Results.BadRequest(new { Error = error });
        }

        var profile = await session.LoadAsync<TenantProfile>(tenantId, ct);
        if (profile is null)
        {
            return Results.NotFound(new { Error = "Tenant was not found." });
        }

        var updated = profile.WithSettings(settings.SystemPrompt, settings.PrivacyPolicyUrl);
        session.Store(updated);
        await session.SaveChangesAsync(ct);
        return Results.Ok(TenantPortalProfile.From(updated));
    }

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

    private static IResult? Gate(ClaimsPrincipal user, string tenantId)
    {
        if (Unauthenticated(user) is { } unauthenticated)
        {
            return unauthenticated;
        }

        if (!TenantIds.IsValid(tenantId))
        {
            return Results.BadRequest(new { Error = "tenantId is not valid." });
        }

        if (!TenantAccess.CanAccess(user, tenantId))
        {
            return Results.Json(
                new { Error = "You do not have access to this organization." },
                statusCode: StatusCodes.Status403Forbidden);
        }

        return null;
    }

    private static IResult? Unauthenticated(ClaimsPrincipal user) =>
        TenantAccess.IsAuthenticated(user) ? null : Results.Unauthorized();

    private static void NoStore(HttpContext httpContext)
    {
        httpContext.Response.Headers.CacheControl = "no-store";
    }
}
