using AutomaticEnvelopes.Api.Common.Configuration;
using AutomaticEnvelopes.Api.Core.Events;
using AutomaticEnvelopes.Api.Features.WhatsAppDispatcher;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace AutomaticEnvelopes.Tests.Features.WhatsAppDispatcher;

public class ReplyGeneratedHandlerTests
{
    [Fact]
    public async Task GivenReplyGenerated_WhenHandled_ThenSendsTheReplyTextOnce()
    {
        var client = new Mock<IWhatsAppClient>();
        WhatsAppTextRequest? sent = null;
        string? phoneNumberId = null;
        client.Setup(item => item.SendMessageAsync(
                It.IsAny<string>(),
                It.IsAny<WhatsAppTextRequest>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, WhatsAppTextRequest, string, CancellationToken>((id, request, _, _) =>
            {
                phoneNumberId = id;
                sent = request;
            })
            .ReturnsAsync(new WhatsAppResponse("whatsapp", [], [new WhatsAppMessageMetadata("wamid.out")]));

        var reply = new ReplyGenerated("wamid.in", "bot-phone", "34600111222", "Hola, el club abre a las 9.", "club-a");

        await ReplyGeneratedHandler.Handle(
            reply,
            client.Object,
            Options.Create(new WhatsAppOptions { AccessToken = "test-token" }),
            NullLogger.Instance,
            CancellationToken.None);

        phoneNumberId.Should().Be("bot-phone");
        sent.Should().NotBeNull();
        sent!.To.Should().Be("34600111222");
        sent.Text.Body.Should().Be("Hola, el club abre a las 9.");
        client.Verify(item => item.SendMessageAsync(
            It.IsAny<string>(),
            It.IsAny<WhatsAppTextRequest>(),
            "Bearer test-token",
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
