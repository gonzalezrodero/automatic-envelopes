using System.Data;
using AutomaticEnvelopes.Api.Core.Entities;
using AutomaticEnvelopes.Api.Core.Events;
using AutomaticEnvelopes.Api.Features.Chat;
using AutomaticEnvelopes.Api.Features.Chat.Handlers;
using AutomaticEnvelopes.Api.Features.Chat.Models;
using AutomaticEnvelopes.Api.Features.Knowledge.Services;
using AutomaticEnvelopes.Api.Features.Portal;
using AutomaticEnvelopes.Api.Features.Tenancy;
using AwesomeAssertions;
using JasperFx.Events;
using Marten;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Wolverine;

namespace AutomaticEnvelopes.Tests.Features.Chat;

public class UnknownReplyDispatchTests
{
    [Fact]
    public async Task GivenAnUnknownReply_WhenHandled_ThenTheMarkerIsRemovedAndTheQuestionIsRecorded()
    {
        var phone = "34600111222";
        var harness = new HandlerHarness(phone, "No tengo ese dato.\n[[UNKNOWN]]");

        await ChatSessionHandler.Handle(
            new AnalyzeChatSession(phone, "club-a", "bot-phone", "¿A qué hora abre?"),
            harness.Store.Object,
            harness.Knowledge.Object,
            harness.Chat.Object,
            harness.Bus.Object,
            NullLogger.Instance,
            CancellationToken.None);

        harness.Appended.Should().NotBeNull();
        harness.Appended!.Text.Should().Be("No tengo ese dato.");
        harness.Appended.Text.Should().NotContain(UnknownReply.Marker);
        harness.Recorded.Should().NotBeNull();
        harness.Recorded!.TenantId.Should().Be("club-a");
        harness.Recorded.ConversationId.Should().Be(PortalIds.ForStream(phone));
        harness.Recorded.Question.Should().Be("¿A qué hora abre?");
    }

    [Fact]
    public async Task GivenOnlyTheMarker_WhenHandled_ThenTheUserSeesTheFallback()
    {
        var phone = "34600111222";
        var harness = new HandlerHarness(phone, UnknownReply.Marker);

        await ChatSessionHandler.Handle(
            new AnalyzeChatSession(phone, "club-a", "bot-phone", "¿Cuánto cuesta?"),
            harness.Store.Object,
            harness.Knowledge.Object,
            harness.Chat.Object,
            harness.Bus.Object,
            NullLogger.Instance,
            CancellationToken.None);

        harness.Appended!.Text.Should().Be(UnknownReply.Fallback);
        harness.Recorded.Should().NotBeNull();
    }

    [Fact]
    public async Task GivenANormalReply_WhenHandled_ThenNothingIsAddedToTheInbox()
    {
        var phone = "34600111222";
        var harness = new HandlerHarness(phone, "Abre a las 9.");

        await ChatSessionHandler.Handle(
            new AnalyzeChatSession(phone, "club-a", "bot-phone", "¿A qué hora abre?"),
            harness.Store.Object,
            harness.Knowledge.Object,
            harness.Chat.Object,
            harness.Bus.Object,
            NullLogger.Instance,
            CancellationToken.None);

        harness.Appended!.Text.Should().Be("Abre a las 9.");
        harness.Recorded.Should().BeNull();
    }

    private sealed class HandlerHarness
    {
        public Mock<IDocumentStore> Store { get; } = new();
        public Mock<IKnowledgeBaseService> Knowledge { get; } = new();
        public Mock<IChatService> Chat { get; } = new();
        public Mock<IMessageBus> Bus { get; } = new();
        public ReplyGenerated? Appended { get; private set; }
        public RecordUnansweredQuestion? Recorded { get; private set; }

        public HandlerHarness(string phone, string reply)
        {
            var events = new Mock<Marten.Events.IEventStoreOperations>();
            events.Setup(item => item.FetchStreamAsync(
                    phone,
                    It.IsAny<long>(),
                    It.IsAny<DateTimeOffset?>(),
                    It.IsAny<long>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<IEvent>());
            events.Setup(item => item.Append(phone, It.IsAny<object[]>()))
                .Callback<string, object[]>((_, appended) => Appended = appended.OfType<ReplyGenerated>().Single());

            var session = new Mock<IDocumentSession>();
            session.Setup(item => item.Events).Returns(events.Object);
            session.Setup(item => item.LoadAsync<TenantProfile>("club-a", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new TenantProfile { Id = "club-a", BotPhoneNumberId = "bot-phone" });
            session.Setup(item => item.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

            Store.Setup(item => item.LightweightSession("club-a", It.IsAny<IsolationLevel>())).Returns(session.Object);
            Knowledge.Setup(item => item.SearchAsync("club-a", It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<DocumentChunk>());
            Chat.Setup(item => item.GetResponseAsync(It.IsAny<string>(), It.IsAny<List<Microsoft.Extensions.AI.ChatMessage>>(), "club-a", It.IsAny<CancellationToken>()))
                .ReturnsAsync(reply);
            Bus.Setup(item => item.InvokeAsync(It.IsAny<ReplyGenerated>(), It.IsAny<CancellationToken>(), null))
                .Returns(Task.CompletedTask);
            Bus.Setup(item => item.InvokeAsync(It.IsAny<RecordUnansweredQuestion>(), It.IsAny<CancellationToken>(), null))
                .Callback<object, CancellationToken, TimeSpan?>((command, _, _) => Recorded = (RecordUnansweredQuestion)command)
                .Returns(Task.CompletedTask);
        }
    }
}
