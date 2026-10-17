using System.Net.Http.Headers;
using Alba;
using AutomaticEnvelopes.Api.Features.AdminAuth;
using AutomaticEnvelopes.Api.Features.Tenancy;
using AwesomeAssertions;

namespace AutomaticEnvelopes.Tests.Features.AdminAuth;

[Collection("Integration")]
public class AdminCsrfIntegrationTests(IntegrationAppFixture fixture)
{
    [Theory]
    [InlineData("ingest", null, 403)]
    [InlineData("ingest", "https://evil.example", 403)]
    [InlineData("ingest", "http://localhost:5173", 200)]
    [InlineData("patch", null, 403)]
    [InlineData("patch", "https://evil.example", 403)]
    [InlineData("patch", "http://localhost:5173", 200)]
    public async Task CookieSession_RequiresAllowedOrigin(string route, string? origin, int status)
    {
        var tenantId = $"csrf-{Guid.NewGuid():N}";
        if (status == 200)
        {
            await fixture.SeedTenantAsync(tenantId, $"phone-{Guid.NewGuid():N}");
        }

        var result = await fixture.Host.Scenario(s =>
        {
            s.WithRequestHeader("Cookie", $"{AdminAuthCookies.AccessToken}=integration-session");
            if (origin is not null)
            {
                s.WithRequestHeader("Origin", origin);
            }

            if (route == "ingest")
            {
                s.Post.MultipartFormData(Notes($"notes-{Guid.NewGuid():N}.txt")).ToUrl($"/api/admin/ingest/{tenantId}");
            }
            else
            {
                s.Patch.Json(new UpdateTenantSettingsRequest
                {
                    SystemPrompt = "Updated persona",
                    PrivacyPolicyUrl = "https://example.com/privacy"
                }).ToUrl($"/tenants/{tenantId}");
            }

            s.StatusCodeShouldBe(status);
        });

        if (route == "patch" && status == 200)
        {
            result.ReadAsText().Should().Contain("Updated persona");
        }
    }

    private static MultipartFormDataContent Notes(string fileName)
    {
        var file = new ByteArrayContent("Portal knowledge for the CSRF check."u8.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        return new MultipartFormDataContent { { file, "file", fileName } };
    }
}
