using AutomaticEnvelopes.Api.Features.Chat;
using AwesomeAssertions;
using Moq;

namespace AutomaticEnvelopes.Tests.Features.Chat;

public class PersonalDataFilterTests
{
    [Fact]
    public async Task Clean_TrimsTheSanitizedText()
    {
        var chat = new Mock<IChatService>();
        chat.Setup(item => item.SanitizeHistoryAsync("Lucía", It.IsAny<CancellationToken>()))
            .ReturnsAsync("  [NAME]  ");
        var filter = new ChatPersonalDataFilter(chat.Object);

        var cleaned = await filter.CleanAsync("Lucía", CancellationToken.None);

        cleaned.Should().Be("[NAME]");
    }
}
