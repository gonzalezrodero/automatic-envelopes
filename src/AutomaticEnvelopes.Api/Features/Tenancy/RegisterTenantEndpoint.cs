using System.Text.Json.Serialization;
using Marten;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Wolverine.Http;

namespace AutomaticEnvelopes.Api.Features.Tenancy;

public sealed class RegisterTenantRequest
{
    [JsonPropertyName("botPhoneNumberId")]
    public string? BotPhoneNumberId { get; set; }

    [JsonPropertyName("systemPrompt")]
    public string? SystemPrompt { get; set; }

    [JsonPropertyName("privacyPolicyUrl")]
    public string? PrivacyPolicyUrl { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("shortName")]
    public string? ShortName { get; set; }

    [JsonPropertyName("city")]
    public string? City { get; set; }

    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonPropertyName("displayPhone")]
    public string? DisplayPhone { get; set; }
}

public static class RegisterTenantEndpoint
{
    [Authorize(Policy = TenantAccess.PlatformAdminPolicy)]
    [WolverinePost("/api/admin/tenants/{tenantId}")]
    [EnableRateLimiting("AdminPolicy")]
    public static async Task<IResult> RegisterTenant(
        string tenantId,
        RegisterTenantRequest? request,
        IDocumentSession session,
        CancellationToken ct)
    {
        if (!TenantIds.IsValid(tenantId))
        {
            return Results.BadRequest(new { Error = "tenantId is not valid." });
        }

        if (request is null || string.IsNullOrWhiteSpace(request.BotPhoneNumberId))
        {
            return Results.BadRequest(new { Error = "botPhoneNumberId is required." });
        }

        var botPhoneNumberId = request.BotPhoneNumberId.Trim();
        var existing = await session.Query<TenantProfile>()
            .AnyAsync(x => x.Id == tenantId || x.BotPhoneNumberId == botPhoneNumberId, ct);

        if (existing)
        {
            return Results.BadRequest(new { Error = "Tenant or BotPhoneNumberId already registered." });
        }

        var profile = new TenantProfile
        {
            Id = tenantId,
            BotPhoneNumberId = botPhoneNumberId,
            SystemPrompt = request.SystemPrompt?.Trim() ?? string.Empty,
            PrivacyPolicyUrl = request.PrivacyPolicyUrl?.Trim() ?? string.Empty,
            Name = request.Name?.Trim() ?? string.Empty,
            ShortName = request.ShortName?.Trim() ?? string.Empty,
            City = request.City?.Trim() ?? string.Empty,
            Kind = request.Kind?.Trim() ?? string.Empty,
            DisplayPhone = request.DisplayPhone?.Trim() ?? string.Empty,
            CreatedAt = DateTime.UtcNow
        };

        session.Store(profile);
        await session.SaveChangesAsync(ct);

        return Results.Created($"/api/admin/tenants/{tenantId}", profile);
    }
}
