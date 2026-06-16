using AutomaticEnvelopes.Api.Features.Tenancy;
using AwesomeAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;

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
            s.Post.Json(profile).ToUrl("/api/admin/tenants");
            s.StatusCodeShouldBe(201);
        });

        // Assert: Verificar respuesta
        var response = result.ReadAsJson<TenantProfile>();
        response.Should().NotBeNull();
        response!.Id.Should().Be(tenantId);

        // Assert: Verificar persistencia en Marten (Tabla Global)
        using var session = fixture.Host.Services.GetRequiredService<IDocumentStore>().QuerySession();
        var stored = await session.LoadAsync<TenantProfile>(tenantId);

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
            s.Post.Json(new TenantProfile { Id = tenantId, BotPhoneNumberId = botPhone1 }).ToUrl("/api/admin/tenants");
            s.StatusCodeShouldBe(201);
        });

        // Act: Intentamos registrar el mismo slug con otro teléfono
        await fixture.Host.Scenario(s =>
        {
            s.Post.Json(new TenantProfile { Id = tenantId, BotPhoneNumberId = botPhone2 }).ToUrl("/api/admin/tenants");

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
            s.Post.Json(new TenantProfile { Id = slug1, BotPhoneNumberId = botPhoneId }).ToUrl("/api/admin/tenants");
            s.StatusCodeShouldBe(201);
        });

        // Act: Intentamos registrar el mismo teléfono con otro slug
        await fixture.Host.Scenario(s =>
        {
            s.Post.Json(new TenantProfile { Id = slug2, BotPhoneNumberId = botPhoneId }).ToUrl("/api/admin/tenants");

            // Assert
            s.StatusCodeShouldBe(400);
        });
    }
}