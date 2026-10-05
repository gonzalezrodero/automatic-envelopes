using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutomaticEnvelopes.Api.Features.Tenancy;
using AwesomeAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace AutomaticEnvelopes.Tests.Features.Tenancy;

[Collection("Integration")]
public class TenantEndpointsIntegrationTests(IntegrationAppFixture fixture)
{
    [Fact]
    public async Task GetTenants_AsAdmin_ReturnsCamelCasePortalProfiles()
    {
        var first = $"club-{Guid.NewGuid():N}";
        var second = $"club-{Guid.NewGuid():N}";
        await SeedAsync(first, "Alpha Club", "https://alpha.example/p");
        await SeedAsync(second, "Beta Club", "https://beta.example/p");

        var result = await fixture.Host.Scenario(s =>
        {
            s.Get.Url("/tenants");
            s.WithRequestHeader("Origin", "http://localhost:5173");
            s.StatusCodeShouldBe(200);
        });

        result.Context.Response.Headers.CacheControl.ToString().Should().Be("no-store");
        result.Context.Response.Headers.AccessControlAllowOrigin.ToString().Should().Be("http://localhost:5173");
        result.Context.Response.Headers.AccessControlAllowCredentials.ToString().Should().Be("true");

        using var json = JsonDocument.Parse(result.ReadAsText());
        var ids = json.RootElement.EnumerateArray().Select(item => item.GetProperty("id").GetString()).ToArray();
        ids.Should().Contain(first);
        ids.Should().Contain(second);
        var match = json.RootElement.EnumerateArray().First(item => item.GetProperty("id").GetString() == first);
        match.GetProperty("name").GetString().Should().Be("Alpha Club");
        match.GetProperty("systemPrompt").GetString().Should().Be("Alpha Club prompt");
        match.GetProperty("privacyPolicyUrl").GetString().Should().Be("https://alpha.example/p");
        match.GetProperty("botPhoneNumberId").GetString().Should().NotBeNullOrWhiteSpace();
        match.TryGetProperty("createdAt", out _).Should().BeFalse();
        match.TryGetProperty("Name", out _).Should().BeFalse();
        match.TryGetProperty("Id", out _).Should().BeFalse();
    }

    [Fact]
    public async Task GetTenants_AsTenantGroup_ReturnsOnlyThatTenant()
    {
        var own = $"own-{Guid.NewGuid():N}";
        var other = $"other-{Guid.NewGuid():N}";
        await SeedAsync(own, "Own", "https://own.example/p");
        await SeedAsync(other, "Other", "https://other.example/p");

        var result = await fixture.Host.Scenario(s =>
        {
            AsTenant(s, own);
            s.Get.Url("/tenants");
            s.StatusCodeShouldBe(200);
        });

        using var json = JsonDocument.Parse(result.ReadAsText());
        var ids = json.RootElement.EnumerateArray().Select(item => item.GetProperty("id").GetString()).ToArray();
        ids.Should().Equal(own);
    }

    [Fact]
    public async Task GetTenants_WithNoTenantGroup_ReturnsAnEmptyList()
    {
        var result = await fixture.Host.Scenario(s =>
        {
            s.RemoveClaim(ClaimTypes.Role);
            s.RemoveClaim("cognito:groups");
            s.Get.Url("/tenants");
            s.StatusCodeShouldBe(200);
        });

        using var json = JsonDocument.Parse(result.ReadAsText());
        json.RootElement.GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task GetTenant_Allowed_ReturnsOneProfile()
    {
        var tenantId = $"club-{Guid.NewGuid():N}";
        await SeedAsync(tenantId, "One", "https://one.example/p");

        var result = await fixture.Host.Scenario(s =>
        {
            AsTenant(s, tenantId);
            s.Get.Url($"/tenants/{tenantId}");
            s.StatusCodeShouldBe(200);
        });

        using var json = JsonDocument.Parse(result.ReadAsText());
        json.RootElement.GetProperty("id").GetString().Should().Be(tenantId);
        json.RootElement.GetProperty("name").GetString().Should().Be("One");
    }

    [Fact]
    public async Task GetTenant_OtherTenant_Returns403_WhenItExistsAndWhenItDoesNot()
    {
        var own = $"own-{Guid.NewGuid():N}";
        var other = $"other-{Guid.NewGuid():N}";
        await SeedAsync(other, "Other", "https://other.example/p");

        await fixture.Host.Scenario(s =>
        {
            AsTenant(s, own);
            s.Get.Url($"/tenants/{other}");
            s.StatusCodeShouldBe(403);
        });

        await fixture.Host.Scenario(s =>
        {
            AsTenant(s, own);
            s.Get.Url($"/tenants/missing-{Guid.NewGuid():N}");
            s.StatusCodeShouldBe(403);
        });
    }

    [Fact]
    public async Task GetTenant_AllowedButMissing_Returns404()
    {
        var tenantId = $"missing-{Guid.NewGuid():N}";

        await fixture.Host.Scenario(s =>
        {
            AsTenant(s, tenantId);
            s.Get.Url($"/tenants/{tenantId}");
            s.StatusCodeShouldBe(404);
        });
    }

    [Fact]
    public async Task GetTenant_InvalidId_Returns400()
    {
        await fixture.Host.Scenario(s =>
        {
            s.Get.Url("/tenants/Not_Valid");
            s.StatusCodeShouldBe(400);
        });
    }

    [Fact]
    public async Task Patch_UpdatesPromptAndUrl_AndDoesNotRewriteDisplayOrBotFields()
    {
        var tenantId = "example-tenant";
        var botPhone = $"phone-{Guid.NewGuid():N}";
        var created = new DateTime(2024, 5, 1, 10, 0, 0, DateTimeKind.Utc);
        await StoreLegacyDocumentAsync(botPhone, created);

        var result = await fixture.Host.Scenario(s =>
        {
            AsTenant(s, tenantId);
            s.Patch.Json(new UpdateTenantSettingsRequest
            {
                SystemPrompt = "  Updated persona.  ",
                PrivacyPolicyUrl = " HTTPS://EXAMPLE.COM/privacy "
            }).ToUrl($"/tenants/{tenantId}");
            s.StatusCodeShouldBe(200);
        });

        using var json = JsonDocument.Parse(result.ReadAsText());
        json.RootElement.GetProperty("systemPrompt").GetString().Should().Be("Updated persona.");
        json.RootElement.GetProperty("privacyPolicyUrl").GetString().Should().Be("https://example.com/privacy");
        json.RootElement.GetProperty("name").GetString().Should().BeEmpty();
        json.RootElement.GetProperty("shortName").GetString().Should().BeEmpty();
        json.RootElement.GetProperty("city").GetString().Should().BeEmpty();
        json.RootElement.GetProperty("kind").GetString().Should().BeEmpty();
        json.RootElement.GetProperty("displayPhone").GetString().Should().BeEmpty();
        json.RootElement.GetProperty("botPhoneNumberId").GetString().Should().Be(botPhone);
        json.RootElement.TryGetProperty("createdAt", out _).Should().BeFalse();

        var stored = await LoadAsync(tenantId);
        stored.SystemPrompt.Should().Be("Updated persona.");
        stored.PrivacyPolicyUrl.Should().Be("https://example.com/privacy");
        stored.BotPhoneNumberId.Should().Be(botPhone);
        stored.CreatedAt.Should().Be(created);
        stored.Name.Should().BeEmpty();
        stored.DisplayPhone.Should().BeEmpty();
    }

    [Fact]
    public async Task Patch_AcceptsLoopbackHttp_AndRejectsAPublicHttpUrlWithoutWriting()
    {
        var tenantId = $"club-{Guid.NewGuid():N}";
        await SeedAsync(tenantId, "Prompt", "https://before.example/p");

        await fixture.Host.Scenario(s =>
        {
            AsTenant(s, tenantId);
            s.Patch.Json(new UpdateTenantSettingsRequest
            {
                SystemPrompt = "Prompt",
                PrivacyPolicyUrl = "http://example.com/privacy"
            }).ToUrl($"/tenants/{tenantId}");
            s.StatusCodeShouldBe(400);
        });

        (await LoadAsync(tenantId)).PrivacyPolicyUrl.Should().Be("https://before.example/p");

        var result = await fixture.Host.Scenario(s =>
        {
            AsTenant(s, tenantId);
            s.Patch.Json(new UpdateTenantSettingsRequest
            {
                SystemPrompt = "Prompt",
                PrivacyPolicyUrl = "http://127.0.0.1:5173/privacy"
            }).ToUrl($"/tenants/{tenantId}");
            s.StatusCodeShouldBe(200);
        });

        using var json = JsonDocument.Parse(result.ReadAsText());
        json.RootElement.GetProperty("privacyPolicyUrl").GetString().Should().Be("http://127.0.0.1:5173/privacy");
    }

    [Fact]
    public async Task Patch_OtherTenant_Returns403_AndLeavesTheDocument()
    {
        var other = $"other-{Guid.NewGuid():N}";
        await SeedAsync(other, "Keep", "https://keep.example/p");

        await fixture.Host.Scenario(s =>
        {
            AsTenant(s, $"own-{Guid.NewGuid():N}");
            s.Patch.Json(new UpdateTenantSettingsRequest
            {
                SystemPrompt = "Stolen",
                PrivacyPolicyUrl = "https://evil.example/p"
            }).ToUrl($"/tenants/{other}");
            s.StatusCodeShouldBe(403);
        });

        var stored = await LoadAsync(other);
        stored.SystemPrompt.Should().Be("Keep prompt");
        stored.PrivacyPolicyUrl.Should().Be("https://keep.example/p");
    }

    [Fact]
    public async Task LegacyMartenJson_WithoutDisplayFields_DeserializesAndLeavesLabelsBlank()
    {
        var store = fixture.Host.Services.GetRequiredService<IDocumentStore>();
        var current = new TenantProfile
        {
            Id = "example-tenant",
            BotPhoneNumberId = "109283746510293",
            SystemPrompt = "Assistant prompt.",
            PrivacyPolicyUrl = "https://example.com/privacy",
            CreatedAt = new DateTime(2024, 5, 1, 10, 0, 0, DateTimeKind.Utc),
            Name = "will be removed"
        };
        var node = JsonNode.Parse(store.Options.Serializer().ToJson(current))!.AsObject();
        node.Remove("Name");
        node.Remove("ShortName");
        node.Remove("City");
        node.Remove("Kind");
        node.Remove("DisplayPhone");

        var legacy = FromJson(store, node.ToJsonString());

        legacy.Name.Should().BeEmpty();
        legacy.DisplayPhone.Should().BeEmpty();
        legacy.BotPhoneNumberId.Should().Be("109283746510293");
        legacy.SystemPrompt.Should().Be("Assistant prompt.");
        var portal = TenantPortalProfile.From(legacy);
        portal.Name.Should().BeEmpty();
        portal.DisplayPhone.Should().BeEmpty();
        portal.BotPhoneNumberId.Should().Be("109283746510293");
    }

    private async Task SeedAsync(string tenantId, string name, string privacyPolicyUrl)
    {
        using var session = fixture.Host.Services.GetRequiredService<IDocumentStore>().LightweightSession();
        session.Store(new TenantProfile
        {
            Id = tenantId,
            BotPhoneNumberId = $"phone-{Guid.NewGuid():N}",
            SystemPrompt = $"{name} prompt",
            PrivacyPolicyUrl = privacyPolicyUrl,
            Name = name,
            ShortName = name,
            City = "Town",
            Kind = "Org"
        });
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task StoreLegacyDocumentAsync(string botPhone, DateTime created)
    {
        var store = fixture.Host.Services.GetRequiredService<IDocumentStore>();
        var document = new TenantProfile
        {
            Id = "example-tenant",
            BotPhoneNumberId = botPhone,
            SystemPrompt = "old",
            PrivacyPolicyUrl = "https://old.example/p",
            CreatedAt = created
        };
        var node = JsonNode.Parse(store.Options.Serializer().ToJson(document))!.AsObject();
        node.Remove("Name");
        node.Remove("ShortName");
        node.Remove("City");
        node.Remove("Kind");
        node.Remove("DisplayPhone");
        var legacy = FromJson(store, node.ToJsonString());

        using var session = store.LightweightSession();
        session.Store(legacy);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<TenantProfile> LoadAsync(string tenantId)
    {
        using var session = fixture.Host.Services.GetRequiredService<IDocumentStore>().QuerySession();
        var stored = await session.LoadAsync<TenantProfile>(tenantId, TestContext.Current.CancellationToken);
        stored.Should().NotBeNull();
        return stored!;
    }

    private static TenantProfile FromJson(IDocumentStore store, string json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return store.Options.Serializer().FromJson<TenantProfile>(stream);
    }

    private static void AsTenant(Alba.Scenario scenario, string tenantId)
    {
        scenario.RemoveClaim(ClaimTypes.Role);
        scenario.RemoveClaim("cognito:groups");
        scenario.WithClaim(new Claim(ClaimTypes.Role, tenantId));
    }
}
