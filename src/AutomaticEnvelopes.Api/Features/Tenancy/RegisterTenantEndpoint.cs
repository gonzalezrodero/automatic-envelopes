using Marten;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Wolverine.Http;

namespace AutomaticEnvelopes.Api.Features.Tenancy;

public static class RegisterTenantEndpoint
{
    [Authorize(Policy = "TenantAdmin")]
    [WolverinePost("/api/admin/tenants/{tenantId}")]
    [EnableRateLimiting("AdminPolicy")]
    public static async Task<IResult> RegisterTenant(
        string tenantId,
        TenantProfile profile,
        IDocumentSession session,
        CancellationToken ct)
    {
        if (!TenantIds.IsValid(tenantId))
        {
            return Results.BadRequest(new { Error = "tenantId is not valid." });
        }

        var existing = await session.Query<TenantProfile>()
            .AnyAsync(x => x.Id == tenantId || x.BotPhoneNumberId == profile.BotPhoneNumberId, ct);

        if (existing)
        {
            return Results.BadRequest(new { Error = "Tenant or BotPhoneNumberId already registered." });
        }

        profile.Id = tenantId;
        session.Store(profile);
        await session.SaveChangesAsync(ct);

        return Results.Created($"/api/admin/tenants/{tenantId}", profile);
    }
}