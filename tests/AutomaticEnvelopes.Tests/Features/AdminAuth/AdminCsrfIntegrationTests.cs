using System.Net.Http.Headers;
using System.Text;
using Alba;
using AutomaticEnvelopes.Api.Features.AdminAuth;
using AutomaticEnvelopes.Api.Features.Tenancy;
using AwesomeAssertions;

namespace AutomaticEnvelopes.Tests.Features.AdminAuth;

[Collection("Integration")]
public class AdminCsrfIntegrationTests(IntegrationAppFixture fixture)
{
    private const string AllowedOrigin = "http://localhost:5173";

    [Fact]
    public async Task MultipartIngest_WithoutOrigin_Returns403()
    {
        await fixture.Host.Scenario(s =>
        {
            WithSessionCookie(s);
            s.Post.MultipartFormData(Notes("notes.txt")).ToUrl($"/api/admin/ingest/{TenantId()}");
            s.StatusCodeShouldBe(403);
        });
    }

    [Fact]
    public async Task MultipartIngest_WithForeignOrigin_Returns403()
    {
        await fixture.Host.Scenario(s =>
        {
            WithSessionCookie(s);
            s.WithRequestHeader("Origin", "https://evil.example");
            s.Post.MultipartFormData(Notes("notes.txt")).ToUrl($"/api/admin/ingest/{TenantId()}");
            s.StatusCodeShouldBe(403);
        });
    }

    [Fact]
    public async Task MultipartIngest_WithAllowedOrigin_ReturnsOk()
    {
        var tenantId = TenantId();
        await fixture.SeedTenantAsync(tenantId, $"phone-{Guid.NewGuid():N}");

        await fixture.Host.Scenario(s =>
        {
            WithSessionCookie(s);
            s.WithRequestHeader("Origin", AllowedOrigin);
            s.Post.MultipartFormData(Notes($"notes-{Guid.NewGuid():N}.txt")).ToUrl($"/api/admin/ingest/{tenantId}");
            s.StatusCodeShouldBeOk();
        });
    }

    [Fact]
    public async Task PatchTenant_WithoutOrigin_Returns403()
    {
        var tenantId = TenantId();
        await fixture.SeedTenantAsync(tenantId, $"phone-{Guid.NewGuid():N}");

        await fixture.Host.Scenario(s =>
        {
            WithSessionCookie(s);
            s.Patch.Json(Settings()).ToUrl($"/tenants/{tenantId}");
            s.StatusCodeShouldBe(403);
        });
    }

    [Fact]
    public async Task PatchTenant_WithForeignOrigin_Returns403()
    {
        var tenantId = TenantId();
        await fixture.SeedTenantAsync(tenantId, $"phone-{Guid.NewGuid():N}");

        await fixture.Host.Scenario(s =>
        {
            WithSessionCookie(s);
            s.WithRequestHeader("Origin", "https://evil.example");
            s.Patch.Json(Settings()).ToUrl($"/tenants/{tenantId}");
            s.StatusCodeShouldBe(403);
        });
    }

    [Fact]
    public async Task PatchTenant_WithAllowedOrigin_ReturnsOk()
    {
        var tenantId = TenantId();
        await fixture.SeedTenantAsync(tenantId, $"phone-{Guid.NewGuid():N}");

        var result = await fixture.Host.Scenario(s =>
        {
            WithSessionCookie(s);
            s.WithRequestHeader("Origin", AllowedOrigin);
            s.Patch.Json(Settings()).ToUrl($"/tenants/{tenantId}");
            s.StatusCodeShouldBe(200);
        });

        result.ReadAsText().Should().Contain("Updated persona");
    }

    private static void WithSessionCookie(Alba.Scenario scenario) =>
        scenario.WithRequestHeader("Cookie", $"{AdminAuthCookies.AccessToken}=integration-session");

    private static string TenantId() => $"csrf-{Guid.NewGuid():N}";

    private static UpdateTenantSettingsRequest Settings() => new()
    {
        SystemPrompt = "Updated persona",
        PrivacyPolicyUrl = "https://example.com/privacy"
    };

    private static MultipartFormDataContent Notes(string fileName)
    {
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes("Portal knowledge for the CSRF check."));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        return new MultipartFormDataContent
        {
            { file, "file", fileName }
        };
    }
}
