using AutomaticEnvelopes.Api.Features.Chat;
using AwesomeAssertions;

namespace AutomaticEnvelopes.Tests.Features.Chat;

public class FirstContactInstructionTests
{
    [Fact]
    public void Instruction_FillsTheTenantNameAndPrivacyUrl()
    {
        var notice = BotPrompts.FirstContactInstruction("  Club Samà  ", "  https://club.example/p  ");

        notice.Should().Contain("translated faithfully into the language the user wrote in");
        notice.Should().Contain("then answer the question");
        notice.Should().Contain("AI assistant on behalf of Club Samà");
        notice.Should().Contain("Club Samà is responsible for the data");
        notice.Should().Contain("the privacy URL unchanged");
        notice.Should().Contain("BORRAR DATOS exactly as written and untranslated");
        notice.Should().Contain("Soy un asistente de inteligencia artificial y escribo en nombre de Club Samà.");
        notice.Should().Contain("Club Samà es responsable de tus datos. Política de privacidad: https://club.example/p. Para borrarlos, escribe BORRAR DATOS.");
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(" ", "")]
    public void Instruction_UsesFallbacksWhenTheTenantHasNoNameOrUrl(string? name, string? url)
    {
        var notice = BotPrompts.FirstContactInstruction(name, url);

        notice.Should().Contain("en nombre de la organización.");
        notice.Should().Contain(BotPrompts.FallbackPrivacyPolicyUrl);
        notice.Should().Contain("escribe BORRAR DATOS.");
    }
}
