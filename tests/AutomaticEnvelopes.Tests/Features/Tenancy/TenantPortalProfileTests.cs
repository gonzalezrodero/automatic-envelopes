using AutomaticEnvelopes.Api.Features.Tenancy;
using AwesomeAssertions;

namespace AutomaticEnvelopes.Tests.Features.Tenancy;

public class TenantPortalProfileTests
{
    [Fact]
    public void From_BlankDisplayFields_StayBlank_AndKeepsBotFields()
    {
        var profile = LegacyDocument();

        var portal = TenantPortalProfile.From(profile);

        portal.Id.Should().Be("example-tenant");
        portal.Name.Should().BeEmpty();
        portal.ShortName.Should().BeEmpty();
        portal.City.Should().BeEmpty();
        portal.Kind.Should().BeEmpty();
        portal.BotPhoneNumberId.Should().Be("109283746510293");
        portal.DisplayPhone.Should().BeEmpty();
        portal.SystemPrompt.Should().Be("Assistant prompt.");
        portal.PrivacyPolicyUrl.Should().Be("https://example.com/privacy");
    }

    [Fact]
    public void From_StoredDisplayFields_AreTrimmedAndReturned()
    {
        var profile = LegacyDocument();
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
    public void From_MissingDisplayFields_DoesNotInventLabelsFromTheId()
    {
        var portal = TenantPortalProfile.From(new TenantProfile
        {
            Id = "other-tenant",
            BotPhoneNumberId = "phone-1"
        });

        portal.Name.Should().BeEmpty();
        portal.ShortName.Should().BeEmpty();
        portal.City.Should().BeEmpty();
        portal.Kind.Should().BeEmpty();
        portal.DisplayPhone.Should().BeEmpty();
        portal.SystemPrompt.Should().BeEmpty();
        portal.PrivacyPolicyUrl.Should().BeEmpty();
    }

    [Fact]
    public void From_WhitespaceDisplayFields_StayBlank()
    {
        var portal = TenantPortalProfile.From(new TenantProfile
        {
            Id = "example-tenant",
            BotPhoneNumberId = "phone-1",
            Name = " ",
            City = "\t"
        });

        portal.Name.Should().BeEmpty();
        portal.City.Should().BeEmpty();
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
            Id = "example-tenant",
            BotPhoneNumberId = "109283746510293",
            SystemPrompt = "old",
            PrivacyPolicyUrl = "https://old.example/p",
            CreatedAt = created,
            Name = "Kept"
        };

        var updated = original.WithSettings("  new persona  ", "https://example.com/privacy");

        updated.Should().NotBeSameAs(original);
        updated.Id.Should().Be(original.Id);
        updated.BotPhoneNumberId.Should().Be(original.BotPhoneNumberId);
        updated.CreatedAt.Should().Be(created);
        updated.Name.Should().Be("Kept");
        updated.SystemPrompt.Should().Be("  new persona  ");
        updated.PrivacyPolicyUrl.Should().Be("https://example.com/privacy");
        original.SystemPrompt.Should().Be("old");
    }

    private static TenantProfile LegacyDocument() => new()
    {
        Id = "example-tenant",
        BotPhoneNumberId = "109283746510293",
        SystemPrompt = "Assistant prompt.",
        PrivacyPolicyUrl = "https://example.com/privacy",
        CreatedAt = new DateTime(2024, 5, 1, 10, 0, 0, DateTimeKind.Utc)
    };
}
