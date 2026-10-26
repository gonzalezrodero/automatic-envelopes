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
            return Results.BadRequest(new { Error = TenantRequest.OrganizationInvalid });
        }

        if (request is null || string.IsNullOrWhiteSpace(request.BotPhoneNumberId))
        {
            return Results.BadRequest(new { Error = "Falta el número de WhatsApp." });
        }

        var botPhoneNumberId = request.BotPhoneNumberId.Trim();
        var existing = await session.Query<TenantProfile>()
            .AnyAsync(x => x.Id == tenantId || x.BotPhoneNumberId == botPhoneNumberId, ct);

        if (existing)
        {
            return Results.BadRequest(new { Error = "Esa organización o ese número ya están registrados." });
        }

        var profile = new TenantProfile
        {
            Id = tenantId,
            BotPhoneNumberId = botPhoneNumberId,
            SystemPrompt = Text(request.SystemPrompt),
            PrivacyPolicyUrl = Text(request.PrivacyPolicyUrl),
            Name = Text(request.Name),
            ShortName = Text(request.ShortName),
            City = Text(request.City),
            Kind = Text(request.Kind),
            DisplayPhone = Text(request.DisplayPhone),
            CreatedAt = DateTime.UtcNow
        };

        session.Store(profile);
        await session.SaveChangesAsync(ct);

        return Results.Created($"/api/admin/tenants/{tenantId}", profile);
    }

    private static string Text(string? value) => value?.Trim() ?? string.Empty;
}
