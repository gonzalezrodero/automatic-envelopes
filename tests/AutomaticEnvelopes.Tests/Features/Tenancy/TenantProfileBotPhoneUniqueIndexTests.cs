using AutomaticEnvelopes.Api.Features.Tenancy;
using AwesomeAssertions;
using JasperFx;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace AutomaticEnvelopes.Tests.Features.Tenancy;

[Collection("Integration")]
public class TenantProfileBotPhoneUniqueIndexTests(IntegrationAppFixture fixture)
{
    [Fact]
    public async Task BotPhoneNumberId_IsGloballyUnique_AndSameTenantUpsertSucceeds()
    {
        const string tenantA = "tenant-a";
        const string tenantB = "tenant-b";
        var phone = $"phone-{Guid.NewGuid():N}";
        var otherPhone = $"phone-{Guid.NewGuid():N}";
        var cancellation = TestContext.Current.CancellationToken;
        var store = fixture.Host.Services.GetRequiredService<IDocumentStore>();

        using (var existing = store.QuerySession())
        {
            (await existing.LoadAsync<TenantProfile>(tenantA, cancellation)).Should().BeNull();
            (await existing.LoadAsync<TenantProfile>(tenantB, cancellation)).Should().BeNull();
        }

        await StoreAsync(store, Profile(tenantA, phone), cancellation);

        var duplicate = async () => await StoreAsync(store, Profile(tenantB, phone), cancellation);
        await ShouldRejectDuplicatePhone(duplicate);

        using (var query = store.QuerySession())
        {
            var matches = await query.Query<TenantProfile>()
                .Where(profile => profile.BotPhoneNumberId == phone)
                .ToListAsync(cancellation);
            matches.Should().ContainSingle().Which.Id.Should().Be(tenantA);
        }

        await StoreAsync(store, Profile(tenantA, phone, "upserted"), cancellation);

        using (var query = store.QuerySession())
        {
            var upserted = await query.LoadAsync<TenantProfile>(tenantA, cancellation);
            upserted.Should().NotBeNull();
            upserted!.BotPhoneNumberId.Should().Be(phone);
            upserted.SystemPrompt.Should().Be("upserted");
        }

        await StoreAsync(store, Profile(tenantB, otherPhone), cancellation);

        var stealPhone = async () => await StoreAsync(store, Profile(tenantB, phone), cancellation);
        await ShouldRejectDuplicatePhone(stealPhone);

        using (var query = store.QuerySession())
        {
            var second = await query.LoadAsync<TenantProfile>(tenantB, cancellation);
            second.Should().NotBeNull();
            second!.BotPhoneNumberId.Should().Be(otherPhone);
        }
    }

    private static async Task ShouldRejectDuplicatePhone(Func<Task> save)
    {
        var thrown = await save.Should().ThrowAsync<DocumentAlreadyExistsException>();
        var postgres = thrown.Which.InnerException.Should().BeOfType<PostgresException>().Which;
        postgres.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
        postgres.ConstraintName.Should().Be("mt_doc_tenantprofile_uidx_bot_phone_number_id");
    }

    private static async Task StoreAsync(IDocumentStore store, TenantProfile profile, CancellationToken cancellation)
    {
        using var session = store.LightweightSession();
        session.Store(profile);
        await session.SaveChangesAsync(cancellation);
    }

    private static TenantProfile Profile(string id, string phone, string systemPrompt = "prompt") => new()
    {
        Id = id,
        BotPhoneNumberId = phone,
        SystemPrompt = systemPrompt,
        PrivacyPolicyUrl = "https://example.com/privacy"
    };
}
