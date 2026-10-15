using AutomaticEnvelopes.Api.Features.Chat;
using AutomaticEnvelopes.Api.Features.Chat.Handlers;
using AutomaticEnvelopes.Api.Features.Chat.Models;
using Moq;

namespace AutomaticEnvelopes.Tests.Features.Chat;

public class UnansweredCommandHandlerTests
{
    [Fact]
    public async Task Record_ForwardsTheQuestionToTheInbox()
    {
        var inbox = new Mock<IUnansweredInbox>();
        var askedAt = DateTimeOffset.UnixEpoch;
        var command = new RecordUnansweredQuestion("club-a", "abc", "¿A qué hora abre?", askedAt);

        await RecordUnansweredQuestionHandler.Handle(command, inbox.Object, CancellationToken.None);

        inbox.Verify(item => item.RecordAsync(
            "club-a",
            "abc",
            "¿A qué hora abre?",
            askedAt,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Forget_ForwardsTheConversationToTheInbox()
    {
        var inbox = new Mock<IUnansweredInbox>();
        var command = new ForgetUnansweredConversation("club-a", "abc");

        await ForgetUnansweredConversationHandler.Handle(command, inbox.Object, CancellationToken.None);

        inbox.Verify(item => item.ForgetConversationAsync("club-a", "abc", It.IsAny<CancellationToken>()), Times.Once);
    }
}
