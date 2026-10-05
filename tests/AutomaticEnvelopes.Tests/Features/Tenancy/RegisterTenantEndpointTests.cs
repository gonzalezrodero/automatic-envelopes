using AutomaticEnvelopes.Api.Features.Tenancy;
using AwesomeAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Claims;

namespace AutomaticEnvelopes.Tests.Features.Tenancy;

[Collection("Integration")]
public class RegisterTenantEndpointTests(IntegrationAppFixture fixture)
{
    [Fact]
    public async Task Post_RegisterTenant_Success_Returns201AndStoresInDb()
    {
        // Arrange
        var tenantId = $"club-{Guid.NewGuid():N}";
        var botPhoneId = $"phone-{Guid.NewGuid():N}";
        var profile = new TenantProfile
        {
            Id = tenantId,
            BotPhoneNumberId = botPhoneId
        };

        // Act
        var result = await fixture.Host.Scenario(s =>
        {
            s.Post.Json(profile).ToUrl($"/api/admin/tenants/{tenantId}");
            s.StatusCodeShouldBe(201);
        });

        // Assert:
        var response = result.ReadAsJson<TenantProfile>();
        response.Should().NotBeNull();
        response!.Id.Should().Be(tenantId);

        // Assert:
        using var session = fixture.Host.Services.GetRequiredService<IDocumentStore>().QuerySession();
        var stored = await session.LoadAsync<TenantProfile>(tenantId, TestContext.Current.CancellationToken);

        stored.Should().NotBeNull();
        stored!.BotPhoneNumberId.Should().Be(botPhoneId);
    }

    [Fact]
    public async Task Post_RegisterTenant_DuplicateId_Returns400()
    {
        // Arrange
        var tenantId = "duplicate-slug";
        var botPhone1 = "phone-1";
        var botPhone2 = "phone-2";

        // Registramos el primero
        await fixture.Host.Scenario(s =>
        {
            s.Post.Json(new TenantProfile { Id = tenantId, BotPhoneNumberId = botPhone1 })
             .ToUrl($"/api/admin/tenants/{tenantId}");
            s.StatusCodeShouldBe(201);
        });

        // Act: Intentamos registrar el mismo slug con otro teléfono
        await fixture.Host.Scenario(s =>
        {
            s.Post.Json(new TenantProfile { Id = tenantId, BotPhoneNumberId = botPhone2 })
             .ToUrl($"/api/admin/tenants/{tenantId}");

            // Assert
            s.StatusCodeShouldBe(400);
        });
    }

    [Fact]
    public async Task Post_RegisterTenant_DuplicateBotPhone_Returns400()
    {
        // Arrange
        var botPhoneId = "unique-phone-id";
        var slug1 = "club-alpha";
        var slug2 = "club-beta";

        // Registramos el primero
        await fixture.Host.Scenario(s =>
        {
            s.Post.Json(new TenantProfile { Id = slug1, BotPhoneNumberId = botPhoneId })
             .ToUrl($"/api/admin/tenants/{slug1}");
            s.StatusCodeShouldBe(201);
        });

        // Act: Intentamos registrar el mismo teléfono con otro slug
        await fixture.Host.Scenario(s =>
        {
            s.Post.Json(new TenantProfile { Id = slug2, BotPhoneNumberId = botPhoneId })
             .ToUrl($"/api/admin/tenants/{slug2}");

            // Assert
            s.StatusCodeShouldBe(400);
        });
    }

    [Fact]
    public async Task Post_RegisterTenant_WithTenantAdminRole_Returns201_AndCoversPolicy()
    {
        // Arrange
        var tenantId = $"club-{Guid.NewGuid():N}";
        var profile = new TenantProfile { Id = tenantId, BotPhoneNumberId = "test-phone" };

        // Act
        await fixture.Host.Scenario(s =>
        {
            s.RemoveClaim(ClaimTypes.Role);
            s.RemoveClaim("cognito:groups");

            s.WithClaim(new Claim(ClaimTypes.Role, tenantId));

            s.Post.Json(profile).ToUrl($"/api/admin/tenants/{tenantId}");
            s.StatusCodeShouldBe(201);
        });
    }

    [Fact]
    public async Task Post_RegisterTenant_WithWrongTenantAdminRole_Returns403_AndCoversPolicy()
    {
        // Arrange
        var tenantId = $"club-{Guid.NewGuid():N}";
        var profile = new TenantProfile { Id = tenantId, BotPhoneNumberId = "test-phone-2" };

        // Act
        await fixture.Host.Scenario(s =>
        {
            s.RemoveClaim(ClaimTypes.Role);
            s.RemoveClaim("cognito:groups");

            s.WithClaim(new Claim(ClaimTypes.Role, "another-random-club"));

            s.Post.Json(profile).ToUrl($"/api/admin/tenants/{tenantId}");
            s.StatusCodeShouldBe(403);
        });
    }

    [Fact]
    public async Task Post_RegisterTenant_InvalidId_Returns400()
    {
        await fixture.Host.Scenario(s =>
        {
            s.Post.Json(new TenantProfile { Id = "Club_Alpha", BotPhoneNumberId = "phone" })
                .ToUrl("/api/admin/tenants/Club_Alpha");
            s.StatusCodeShouldBe(400);
        });
    }
}