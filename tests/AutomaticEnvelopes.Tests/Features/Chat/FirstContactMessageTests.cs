using AutomaticEnvelopes.Api.Features.Chat;
using AutomaticEnvelopes.Api.Features.Tenancy;
using AwesomeAssertions;

namespace AutomaticEnvelopes.Tests.Features.Chat;

public class FirstContactMessageTests
{
    private const string Name = "Club Samà";
    private const string Url = "https://club.example/privacidad";

    [Fact]
    public void Compose_DefaultsToSpanish()
    {
        var message = FirstContactMessage.Compose(Name, Url);

        message.Should().Be(FirstContactMessage.Compose(Name, Url, FirstContactLanguage.Spanish));
        ShouldReadAsNotice(message, Name, Url, "inteligencia artificial", "escribo en nombre de");
        message.Should().Contain("Aquí estoy para ayudarte.");
        message.Should().Contain("es responsable de tus datos");
        message.Should().Contain("Política de privacidad:");
        message.Should().Contain("Para borrarlos, escribe BORRAR DATOS.");
    }

    [Fact]
    public void Compose_WritesTheCatalanNotice()
    {
        var message = FirstContactMessage.Compose(Name, Url, FirstContactLanguage.Catalan);

        ShouldReadAsNotice(message, Name, Url, "intel·ligència artificial", "escric en nom de");
        message.Should().Contain("Sóc aquí per ajudar-te.");
        message.Should().Contain("és responsable de les teves dades");
        message.Should().Contain("Política de privacitat:");
        message.Should().Contain("Per esborrar-les, escriu BORRAR DATOS.");
    }

    [Fact]
    public void Compose_WritesTheEnglishNotice()
    {
        var message = FirstContactMessage.Compose(Name, Url, FirstContactLanguage.English);

        ShouldReadAsNotice(message, Name, Url, "AI assistant", "on behalf of");
        message.Should().Contain("I'm here to help.");
        message.Should().Contain("is responsible for your data");
        message.Should().Contain("Privacy policy:");
        message.Should().Contain("To delete your data, send BORRAR DATOS.");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Compose_UsesTheFallbackPrivacyUrlWhenTheTenantHasNone(string? url)
    {
        var message = FirstContactMessage.Compose(Name, url);

        message.Should().Contain(FirstContactMessage.FallbackPrivacyPolicyUrl);
        message.Should().Contain(Name);
    }

    [Fact]
    public void Compose_KeepsAConfiguredUrlAsStored()
    {
        const string legacy = "http://static.example/POLITICA+DE+PRIVACIDAD.pdf";

        var trimmed = FirstContactMessage.Compose(Name, "  " + Url + "  ");
        trimmed.Should().Contain(Url);
        trimmed.Should().NotContain(Url + " ");
        FirstContactMessage.Compose(Name, legacy).Should().Contain(legacy);
    }

    [Theory]
    [InlineData(FirstContactLanguage.Spanish, "la organización")]
    [InlineData(FirstContactLanguage.Catalan, "l'organització")]
    [InlineData(FirstContactLanguage.English, "the organisation")]
    public void Compose_NamesTheOrganisationWhenTheTenantNameIsBlank(FirstContactLanguage language, string fallback)
    {
        var message = FirstContactMessage.Compose(" \n ", null, language);

        message.Should().Contain(fallback);
        message.Split('\n').Should().HaveCount(3);
    }

    [Fact]
    public void Compose_CollapsesANameThatHasExtraWhitespace()
    {
        var message = FirstContactMessage.Compose("  Club \n  Samà  ", Url);

        message.Should().Contain("Club Samà");
        message.Should().NotContain("  ");
    }

    [Fact]
    public void Compose_KeepsBracesInTheTenantName()
    {
        var message = FirstContactMessage.Compose("Club {Samà}", Url);

        message.Should().Contain("Club {Samà}");
        message.Should().Contain(Url);
    }

    [Fact]
    public void Compose_UsesSpanishForAnUnknownLanguage()
    {
        var unknown = FirstContactMessage.Compose(Name, Url, (FirstContactLanguage)99);

        unknown.Should().Be(FirstContactMessage.Compose(Name, Url));
    }

    [Fact]
    public void For_UsesTheTenantNameAndPrivacyUrl()
    {
        var tenant = new TenantProfile
        {
            Id = "club-sama",
            BotPhoneNumberId = "bot",
            Name = "  Club Samà  ",
            ShortName = "Samà",
            PrivacyPolicyUrl = Url
        };

        FirstContactMessage.For(tenant).Should().Be(FirstContactMessage.Compose("Club Samà", Url));
    }

    [Fact]
    public void For_UsesTheShortNameWhenTheNameIsBlank()
    {
        var tenant = new TenantProfile
        {
            Id = "club-sama",
            BotPhoneNumberId = "bot",
            Name = " ",
            ShortName = "Samà",
            PrivacyPolicyUrl = " "
        };

        var message = FirstContactMessage.For(tenant);

        message.Should().Contain("Samà");
        message.Should().Contain(FirstContactMessage.FallbackPrivacyPolicyUrl);
        message.Should().NotContain("la organización");
    }

    private static void ShouldReadAsNotice(string message, string name, string url, string disclosure, string behalf)
    {
        var lines = message.Split('\n');
        lines.Should().HaveCount(3);
        lines.Should().OnlyContain(line => line.Trim().Length > 0);

        message.Split("👋").Should().HaveCount(2);
        lines[0].Should().Contain("👋").And.Contain(disclosure).And.Contain(behalf).And.Contain(name);
        lines[0].Should().NotContain(url).And.NotContain(BotPrompts.DeleteCommand);
        lines[1].Should().NotContain(url).And.NotContain(BotPrompts.DeleteCommand);
        lines[2].Should().Contain(name).And.Contain(url).And.Contain(BotPrompts.DeleteCommand);
    }
}
