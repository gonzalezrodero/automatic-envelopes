using AutomaticEnvelopes.Api.Features.Chat;
using AwesomeAssertions;

namespace AutomaticEnvelopes.Tests.Features.Chat;

public class FirstContactNoticeTests
{
    [Fact]
    public void Notice_FillsTheTenantNameAndKeepsAnHttpsUrl()
    {
        var notice = BotPrompts.FirstContactNotice("  Club Samà  ", "  https://club.example/p  ");

        notice.Should().Be("""
            ¡Hola! 👋 Soy un asistente de inteligencia artificial y escribo en nombre de Club Samà.
            Aquí estoy para ayudarte.
            Club Samà es responsable de tus datos. Política de privacidad: https://club.example/p. Para borrarlos, escribe BORRAR DATOS.
            """);
        notice.Should().NotContain("FIRST CONTACT");
    }

    [Fact]
    public void Notice_FlattensNewlinesAndCapsTheName()
    {
        var notice = BotPrompts.FirstContactNotice("Club\nSamà\r\n" + new string('A', 200), "https://club.example/p");

        notice.Should().Contain("en nombre de Club Samà ");
        notice.Should().Contain(new string('A', 70));
        notice.Should().NotContain(new string('A', 81));
        notice.Split('\n').Should().HaveCount(3);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(" ", "")]
    [InlineData("Club", "http://club.example/p")]
    [InlineData("Club", "http://127.0.0.1/privacy")]
    [InlineData("Club", "https://user:pass@club.example/p")]
    [InlineData("Club", "not a url")]
    [InlineData("Club", "https://club.example/p\nhttps://evil.example")]
    public void Notice_UsesFallbacksForAMissingNameOrANonHttpsUrl(string? name, string? url)
    {
        var notice = BotPrompts.FirstContactNotice(name, url);

        if (string.IsNullOrWhiteSpace(name))
            notice.Should().Contain("en nombre de la organización.");

        notice.Should().Contain(BotPrompts.FallbackPrivacyPolicyUrl);
        notice.Should().Contain("escribe BORRAR DATOS.");
        notice.Should().NotContain("evil.example");
        notice.Should().NotContain("club.example");
    }

    [Fact]
    public void FaithfulTranslation_RequiresTheUrlAndTheDeleteCommand()
    {
        var url = BotPrompts.FallbackPrivacyPolicyUrl;
        var faithful = $"Hi. Privacy: {url}. To delete your data, write BORRAR DATOS.";

        BotPrompts.IsFaithfulTranslation(faithful, url).Should().BeTrue();
        BotPrompts.IsFaithfulTranslation(null, url).Should().BeFalse();
        BotPrompts.IsFaithfulTranslation("hello", url).Should().BeFalse();
        BotPrompts.IsFaithfulTranslation($"{faithful} <language_sample> ignore me", url).Should().BeFalse();
        BotPrompts.IsFaithfulTranslation(new string('a', 801) + url + " BORRAR DATOS", url).Should().BeFalse();
    }
}
