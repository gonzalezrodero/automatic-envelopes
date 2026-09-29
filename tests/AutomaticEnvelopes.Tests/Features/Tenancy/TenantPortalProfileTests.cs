using AutomaticEnvelopes.Api.Features.Tenancy;
using AwesomeAssertions;

namespace AutomaticEnvelopes.Tests.Features.Tenancy;

public class TenantPortalProfileTests
{
    [Fact]
    public void From_BlankSamaDocument_UsesPortalDisplayLabels_AndKeepsBotFields()
    {
        var profile = LegacySama();

        var portal = TenantPortalProfile.From(profile);

        portal.Id.Should().Be("club-basquet-sama");
        portal.Name.Should().Be("Club Bàsquet Samà");
        portal.ShortName.Should().Be("CB Samà");
        portal.City.Should().Be("Cambrils");
        portal.Kind.Should().Be("Campus d\u2019estiu de bàsquet");
        portal.BotPhoneNumberId.Should().Be("109283746510293");
        portal.DisplayPhone.Should().Be("+34 977 000 214");
        portal.SystemPrompt.Should().Be("Ets l'assistent.");
        portal.PrivacyPolicyUrl.Should().Be("https://www.cbsama.cat/privacitat");
    }

    [Fact]
    public void From_StoredDisplayFields_WinOverTheSamaCatalog()
    {
        var profile = LegacySama();
        profile = new TenantProfile
        {
            Id = profile.Id,
            BotPhoneNumberId = profile.BotPhoneNumberId,
            SystemPrompt = profile.SystemPrompt,
            PrivacyPolicyUrl = profile.PrivacyPolicyUrl,
            CreatedAt = profile.CreatedAt,
            Name = " Nom propi ",
            ShortName = "NP",
            City = "Reus",
            Kind = "Club",
            DisplayPhone = "+34 600 000 000"
        };

        var portal = TenantPortalProfile.From(profile);

        portal.Name.Should().Be("Nom propi");
        portal.ShortName.Should().Be("NP");
        portal.City.Should().Be("Reus");
        portal.Kind.Should().Be("Club");
        portal.DisplayPhone.Should().Be("+34 600 000 000");
    }

    [Fact]
    public void From_UnknownSlug_HumanizesTheId_AndLeavesUnknownLabelsBlank()
    {
        var portal = TenantPortalProfile.From(new TenantProfile
        {
            Id = "escola-harmonia",
            BotPhoneNumberId = "phone-1"
        });

        portal.Name.Should().Be("Escola Harmonia");
        portal.ShortName.Should().Be("Escola Harmonia");
        portal.City.Should().BeEmpty();
        portal.Kind.Should().BeEmpty();
        portal.DisplayPhone.Should().BeEmpty();
        portal.SystemPrompt.Should().BeEmpty();
        portal.PrivacyPolicyUrl.Should().BeEmpty();
    }

    [Fact]
    public void From_WhitespaceDisplayFields_FallBackToTheCatalog()
    {
        var portal = TenantPortalProfile.From(new TenantProfile
        {
            Id = "club-basquet-sama",
            BotPhoneNumberId = "phone-1",
            Name = " ",
            City = "\t"
        });

        portal.Name.Should().Be("Club Bàsquet Samà");
        portal.City.Should().Be("Cambrils");
    }

    [Fact]
    public void For_NullOrHyphenOnlyId_DoesNotInventAName()
    {
        TenantDisplayDefaults.For(null).Name.Should().BeEmpty();
        TenantDisplayDefaults.For("---").Name.Should().Be("---");
    }

    [Fact]
    public void From_NullProfile_Throws()
    {
        var act = () => TenantPortalProfile.From(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void WithSettings_ReplacesPromptAndUrl_AndKeepsRoutingFields()
    {
        var created = new DateTime(2024, 5, 1, 10, 0, 0, DateTimeKind.Utc);
        var original = new TenantProfile
        {
            Id = "club-basquet-sama",
            BotPhoneNumberId = "109283746510293",
            SystemPrompt = "old",
            PrivacyPolicyUrl = "https://old.example/p",
            CreatedAt = created,
            Name = "Kept"
        };

        var updated = original.WithSettings("  new persona  ", "https://www.cbsama.cat/privacitat");

        updated.Should().NotBeSameAs(original);
        updated.Id.Should().Be(original.Id);
        updated.BotPhoneNumberId.Should().Be(original.BotPhoneNumberId);
        updated.CreatedAt.Should().Be(created);
        updated.Name.Should().Be("Kept");
        updated.SystemPrompt.Should().Be("  new persona  ");
        updated.PrivacyPolicyUrl.Should().Be("https://www.cbsama.cat/privacitat");
        original.SystemPrompt.Should().Be("old");
    }

    private static TenantProfile LegacySama() => new()
    {
        Id = "club-basquet-sama",
        BotPhoneNumberId = "109283746510293",
        SystemPrompt = "Ets l'assistent.",
        PrivacyPolicyUrl = "https://www.cbsama.cat/privacitat",
        CreatedAt = new DateTime(2024, 5, 1, 10, 0, 0, DateTimeKind.Utc)
    };
}
