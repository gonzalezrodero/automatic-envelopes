using System.Data;
using AutomaticEnvelopes.Api.Core.Events;
using AutomaticEnvelopes.Api.Features.Chat;
using AutomaticEnvelopes.Api.Features.Chat.Handlers;
using AutomaticEnvelopes.Api.Features.Chat.Models;
using AwesomeAssertions;
using JasperFx.Events;
using Marten;
using Moq;
using Wolverine;

namespace AutomaticEnvelopes.Tests.Features.Chat;

public class DeleteChatHistoryHandlerTests
{
    [Fact]
    public async Task GivenUserMessageAndReply_WhenHandled_ThenStoresAnonymizedChatAndRemovesTheStream()
    {
        const string phone = "34600111222";
        const string tenantId = "club-a";
        const string sanitized = "User: [NAME]\nBot: Hola";

        var user = new MessageReceived("wamid.1", phone, "Me llamo Ana", tenantId, "bot-phone", DateTimeOffset.UtcNow);
        var reply = new ReplyGenerated("wamid.1", "bot-phone", phone, "Hola Ana", tenantId);
        IReadOnlyList<IEvent> stream =
        [
            new Event<MessageReceived>(user),
            new Event<ReplyGenerated>(reply)
        ];

        var events = new Mock<Marten.Events.IEventStoreOperations>();
        events.Setup(item => item.FetchStreamAsync(
                phone,
                It.IsAny<long>(),
                It.IsAny<DateTimeOffset?>(),
                It.IsAny<long>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(stream);

        AnonymizedChat? stored = null;
        var sql = new List<(string Command, object[] Args)>();
        var session = new Mock<IDocumentSession>();
        session.Setup(item => item.Events).Returns(events.Object);
        session.Setup(item => item.Store(It.IsAny<AnonymizedChat[]>()))
            .Callback<AnonymizedChat[]>(items => stored = items.Single());
        session.Setup(item => item.QueueSqlCommand(It.IsAny<string>(), It.IsAny<object[]>()))
            .Callback<string, object[]>((command, args) => sql.Add((command, args)));
        session.Setup(item => item.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var store = new Mock<IDocumentStore>();
        store.Setup(item => item.LightweightSession(tenantId, It.IsAny<IsolationLevel>())).Returns(session.Object);

        string? transcript = null;
        var chat = new Mock<IChatService>();
        chat.Setup(item => item.SanitizeHistoryAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((raw, _) => transcript = raw)
            .ReturnsAsync(sanitized);

        var bus = new Mock<IMessageBus>();
        bus.Setup(item => item.InvokeAsync(It.IsAny<ReplyGenerated>(), It.IsAny<CancellationToken>(), null))
            .Returns(Task.CompletedTask);

        await DeleteChatHistoryHandler.Handle(
            new DeleteChatHistoryCommand(phone, tenantId, "wamid.1", "bot-phone"),
            store.Object,
            chat.Object,
            bus.Object,
            CancellationToken.None);

        transcript.Should().Be("User: Me llamo Ana\nBot: Hola Ana");
        stored.Should().NotBeNull();
        stored!.Transcript.Should().Be(sanitized);
        stored.Transcript.Should().NotContain("Ana");
        sql.Should().Contain(item =>
            item.Command.Contains("mt_events", StringComparison.Ordinal) &&
            item.Args.Length == 2 &&
            (string)item.Args[0] == phone &&
            (string)item.Args[1] == tenantId);
        sql.Should().Contain(item =>
            item.Command.Contains("mt_streams", StringComparison.Ordinal) &&
            (string)item.Args[0] == phone);
        session.Verify(item => item.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}
