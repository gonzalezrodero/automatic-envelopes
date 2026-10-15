using System.Security.Claims;
using AutomaticEnvelopes.Api.Features.Chat;
using AutomaticEnvelopes.Api.Features.Portal;
using AutomaticEnvelopes.Api.Features.Tenancy;
using AwesomeAssertions;
using Marten;
using Microsoft.AspNetCore.Http;
using Moq;

namespace AutomaticEnvelopes.Tests.Features.Portal;

public class UnansweredEndpointTests
{
    [Fact]
    public async Task Answer_RejectsAnIdThatIsNotAGuid()
    {
        var status = await Answer("not-a-guid", new AnswerUnansweredRequest { Answer = "A las 9." }, Inbox(null));
        status.Should().Be(400);
    }

    [Fact]
    public async Task Answer_RejectsAnAnswerThatIsTooLong()
    {
        var status = await Answer(
            Guid.NewGuid().ToString("D"),
            new AnswerUnansweredRequest { Answer = new string('a', 2001) },
            Inbox(null));
        status.Should().Be(400);
    }

    [Fact]
    public async Task Answer_RejectsAnEmptyAnswer()
    {
        var status = await Answer(Guid.NewGuid().ToString("D"), new AnswerUnansweredRequest { Answer = "  " }, Inbox(null));
        status.Should().Be(400);
    }

    [Fact]
    public async Task Answer_ReturnsNotFoundWhenTheQuestionIsGone()
    {
        var status = await Answer(Guid.NewGuid().ToString("D"), new AnswerUnansweredRequest { Answer = "A las 9." }, Inbox(null));
        status.Should().Be(404);
    }

    [Fact]
    public async Task Answer_ReturnsTheSavedQuestion()
    {
        var id = Guid.NewGuid();
        var saved = new UnansweredQuestion
        {
            Id = id,
            ConversationId = "abc",
            Question = "¿A qué hora abre?",
            AskedAt = DateTimeOffset.UnixEpoch,
            Answer = "A las 9."
        };

        var result = await PortalEndpoints.AnswerUnanswered(
            "club-a",
            id.ToString("D"),
            new AnswerUnansweredRequest { Answer = "A las 9." },
            Context(),
            Session(),
            Inbox(saved),
            CancellationToken.None);

        Status(result).Should().Be(200);
        var body = result.Should().BeAssignableTo<IValueHttpResult<PortalUnansweredQuestion>>().Subject.Value;
        body!.Id.Should().Be(id.ToString("D"));
        body.Question.Should().Be("¿A qué hora abre?");
        body.ConversationId.Should().Be("abc");
    }

    [Fact]
    public async Task List_ReturnsTheOpenQuestions()
    {
        var question = new UnansweredQuestion
        {
            Id = Guid.NewGuid(),
            ConversationId = "abc",
            Question = "¿Dónde es?",
            AskedAt = DateTimeOffset.UnixEpoch
        };
        var inbox = new Mock<IUnansweredInbox>();
        inbox.Setup(item => item.ListOpenAsync("club-a", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { question });

        var result = await PortalEndpoints.Unanswered(
            "club-a",
            User(),
            Context(),
            Session(),
            inbox.Object,
            CancellationToken.None);

        Status(result).Should().Be(200);
        var body = result.Should().BeAssignableTo<IValueHttpResult<PortalUnansweredQuestion[]>>().Subject.Value;
        body.Should().ContainSingle(item => item.Question == "¿Dónde es?");
    }

    [Fact]
    public async Task Answered_ReturnsTheClosedQuestionsWithoutTheConversation()
    {
        var question = new UnansweredQuestion
        {
            Id = Guid.NewGuid(),
            ConversationId = "abc",
            Question = "¿Dónde es?",
            AskedAt = DateTimeOffset.UnixEpoch,
            Answer = "En la pista.",
            AnsweredAt = DateTimeOffset.UnixEpoch
        };
        var inbox = new Mock<IUnansweredInbox>();
        inbox.Setup(item => item.ListAnsweredAsync("club-a", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { question });

        var result = await PortalEndpoints.Answered(
            "club-a",
            User(),
            Context(),
            Session(),
            inbox.Object,
            CancellationToken.None);

        Status(result).Should().Be(200);
        var body = result.Should().BeAssignableTo<IValueHttpResult<PortalAnsweredQuestion[]>>().Subject.Value;
        body.Should().ContainSingle(item => item.Question == "¿Dónde es?" && item.Answer == "En la pista.");
    }

    [Fact]
    public async Task Revise_ReturnsTheCorrectedAnswer()
    {
        var id = Guid.NewGuid();
        var saved = new UnansweredQuestion
        {
            Id = id,
            Question = "¿A qué hora abre?",
            Answer = "A las 10.",
            AnsweredAt = DateTimeOffset.UnixEpoch
        };
        var inbox = new Mock<IUnansweredInbox>();
        inbox.Setup(item => item.ReviseAsync("club-a", id, "A las 10.", It.IsAny<CancellationToken>()))
            .ReturnsAsync(saved);

        var result = await PortalEndpoints.ReviseUnanswered(
            "club-a",
            id.ToString("D"),
            new AnswerUnansweredRequest { Answer = "A las 10." },
            Context(),
            Session(),
            inbox.Object,
            CancellationToken.None);

        Status(result).Should().Be(200);
        var body = result.Should().BeAssignableTo<IValueHttpResult<PortalAnsweredQuestion>>().Subject.Value;
        body!.Answer.Should().Be("A las 10.");
    }

    [Fact]
    public async Task Revise_RejectsAnEmptyAnswer()
    {
        var result = await PortalEndpoints.ReviseUnanswered(
            "club-a",
            Guid.NewGuid().ToString("D"),
            new AnswerUnansweredRequest { Answer = "  " },
            Context(),
            Session(),
            Inbox(null),
            CancellationToken.None);

        Status(result).Should().Be(400);
    }

    [Fact]
    public async Task Withdraw_ReturnsNoContentWhenTheAnswerIsRemoved()
    {
        var id = Guid.NewGuid();
        var inbox = new Mock<IUnansweredInbox>();
        inbox.Setup(item => item.WithdrawAsync("club-a", id, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var result = await PortalEndpoints.WithdrawUnanswered(
            "club-a",
            id.ToString("D"),
            Context(),
            Session(),
            inbox.Object,
            CancellationToken.None);

        Status(result).Should().Be(204);
    }

    [Fact]
    public async Task Withdraw_ReturnsNotFoundWhenTheQuestionIsOpen()
    {
        var inbox = new Mock<IUnansweredInbox>();
        inbox.Setup(item => item.WithdrawAsync("club-a", It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var result = await PortalEndpoints.WithdrawUnanswered(
            "club-a",
            Guid.NewGuid().ToString("D"),
            Context(),
            Session(),
            inbox.Object,
            CancellationToken.None);

        Status(result).Should().Be(404);
    }

    private static async Task<int> Answer(string questionId, AnswerUnansweredRequest body, IUnansweredInbox inbox)
    {
        var result = await PortalEndpoints.AnswerUnanswered(
            "club-a",
            questionId,
            body,
            Context(),
            Session(),
            inbox,
            CancellationToken.None);
        return Status(result);
    }

    private static IUnansweredInbox Inbox(UnansweredQuestion? saved)
    {
        var inbox = new Mock<IUnansweredInbox>();
        inbox.Setup(item => item.AnswerAsync("club-a", It.IsAny<Guid>(), "A las 9.", It.IsAny<CancellationToken>()))
            .ReturnsAsync(saved);
        return inbox.Object;
    }

    private static IDocumentSession Session()
    {
        var session = new Mock<IDocumentSession>();
        session.Setup(item => item.LoadAsync<TenantProfile>("club-a", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TenantProfile { Id = "club-a", BotPhoneNumberId = "1" });
        return session.Object;
    }

    private static DefaultHttpContext Context() => new() { User = User() };

    private static ClaimsPrincipal User()
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Role, "club-a")], "test");
        return new ClaimsPrincipal(identity);
    }

    private static int Status(IResult result) =>
        result.Should().BeAssignableTo<IStatusCodeHttpResult>().Subject.StatusCode!.Value;
}
