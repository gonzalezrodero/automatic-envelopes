using Marten;
using Wolverine.Http;

namespace AutomaticEnvelopes.Api.Features.Tenancy;

public static class RegisterTenantEndpoint
{
    [WolverinePost("/api/admin/tenants")]
    public static async Task<IResult> RegisterTenant(
        TenantProfile profile,
        IDocumentSession session,
        CancellationToken ct)
    {
        var existing = await session.Query<TenantProfile>()
            .AnyAsync(x => x.Id == profile.Id || x.BotPhoneNumberId == profile.BotPhoneNumberId, ct);

        if (existing)
        {
            return Results.BadRequest(new { Error = "Tenant or BotPhoneNumberId already registered." });
        }

        session.Store(profile);
        await session.SaveChangesAsync(ct);

        return Results.Created($"/api/admin/tenants/{profile.Id}", profile);
    }
}