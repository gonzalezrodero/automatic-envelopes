using AutomaticEnvelopes.Api.Features.Chat.Handlers;
using AwesomeAssertions;

namespace AutomaticEnvelopes.Tests.Features.Chat;

public class ChatSessionDeleteRequestTests
{
    [Theory]
    [InlineData("BORRAR DATOS")]
    [InlineData("esborrar dades")]
    [InlineData("DELETE data")]
    [InlineData("Hola\nBORRAR DATOS")]
    [InlineData("Hola\n\n  delete data  ")]
    public void LastLineThatIsADeleteCommand_IsADelete(string combinedText)
    {
        ChatSessionHandler.IsDeleteRequest(combinedText).Should().BeTrue();
    }

    [Theory]
    [InlineData("Hola")]
    [InlineData("BORRAR DATOS\nHola")]
    [InlineData("Please BORRAR DATOS now")]
    [InlineData("")]
    public void AnyOtherWindow_IsNotADelete(string combinedText)
    {
        ChatSessionHandler.IsDeleteRequest(combinedText).Should().BeFalse();
    }
}
