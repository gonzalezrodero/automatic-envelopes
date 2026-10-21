using System.Data;
using AutomaticEnvelopes.Api.Core.Events;
using AutomaticEnvelopes.Api.Features.Chat;
using AutomaticEnvelopes.Api.Features.Chat.Handlers;
using AutomaticEnvelopes.Api.Features.Chat.Models;
using AutomaticEnvelopes.Api.Features.Knowledge.Services;
using AutomaticEnvelopes.Api.Features.Tenancy;
using AutomaticEnvelopes.Api.Features.Usage;
using AutomaticEnvelopes.Tests;
using AwesomeAssertions;
using Marten;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Wolverine;

namespace AutomaticEnvelopes.Tests.Features.Chat;

public class MonthlyLimitTests
{
    [Fact]
    public async Task AtTheLimit_DoesNotCallBedrockForTheAnswer_AndRepliesInTheUserLanguage()
    {
        var profile = new TenantProfile
        {
            Id = "club-a",
            BotPhoneNumberId = "bot-phone",
            Name = "Club",
            PrivacyPolicyUrl = "https://club.example/p",
            MonthlyResponseLimit = 0
        };
        var harness = new LimitHarness(profile, firstContact: true);

        await ChatSessionHandler.Handle(
            new AnalyzeChatSession("34600111222", profile.Id, profile.BotPhoneNumberId, "Quina és la contrasenya?"),
            harness.Store.Object,
            harness.Knowledge.Object,
            harness.Chat.Object,
            TestUsage.Meter(harness.Store.Object),
            harness.Bus.Object,
            NullLogger.Instance,
            CancellationToken.None);

        harness.Chat.Verify(
            item => item.GetResponseAsync(
                It.IsAny<string>(),
                It.IsAny<List<ChatMessage>>(),
                It.IsAny<string>(),
                It.IsAny<IReadOnlyCollection<string>?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        harness.Chat.Verify(
            item => item.TranslateNoticeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
        harness.Knowledge.Verify(
            item => item.SearchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
        harness.Appended.Should().NotBeNull();
        harness.Appended!.Text.Should().Contain("BORRAR DATOS");
        harness.Appended.Text.Should().Contain(UsageMessages.Catalan);
    }

    [Fact]
    public async Task DeleteCommand_StillRunsWhenTheMonthlyLimitIsZero()
    {
        var profile = new TenantProfile
        {
            Id = "club-a",
            BotPhoneNumberId = "bot-phone",
            MonthlyResponseLimit = 0
        };
        var harness = new LimitHarness(profile, firstContact: false);

        await ChatSessionHandler.Handle(
            new AnalyzeChatSession("34600111222", profile.Id, profile.BotPhoneNumberId, "BORRAR DATOS"),
            harness.Store.Object,
            harness.Knowledge.Object,
            harness.Chat.Object,
            TestUsage.Meter(harness.Store.Object),
            harness.Bus.Object,
            NullLogger.Instance,
            CancellationToken.None);

        harness.Chat.Verify(
            item => item.GetResponseAsync(
                It.IsAny<string>(),
                It.IsAny<List<ChatMessage>>(),
                It.IsAny<string>(),
                It.IsAny<IReadOnlyCollection<string>?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        harness.Bus.Verify(
            item => item.InvokeAsync(It.IsAny<DeleteChatHistoryCommand>(), It.IsAny<CancellationToken>(), null),
            Times.Once);
    }

    private sealed class LimitHarness
    {
        public Mock<IDocumentStore> Store { get; } = new();
        public Mock<IKnowledgeBaseService> Knowledge { get; } = new();
        public Mock<IChatService> Chat { get; } = new();
        public Mock<IMessageBus> Bus { get; } = new();
        public ReplyGenerated? Appended { get; private set; }

        public LimitHarness(TenantProfile profile, bool firstContact)
        {
            var events = new Mock<Marten.Events.IEventStoreOperations>();
            events.Setup(item => item.FetchStreamAsync(
                    It.IsAny<string>(),
                    It.IsAny<long>(),
                    It.IsAny<DateTimeOffset?>(),
                    It.IsAny<long>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(firstContact
                    ? []
                    : [new JasperFx.Events.Event<ReplyGenerated>(new ReplyGenerated("old", profile.BotPhoneNumberId, "34600", "Hola", profile.Id))]);
            events.Setup(item => item.Append(It.IsAny<string>(), It.IsAny<object[]>()))
                .Callback<string, object[]>((_, appended) => Appended = appended.OfType<ReplyGenerated>().Single());

            var session = new Mock<IDocumentSession>();
            session.Setup(item => item.Events).Returns(events.Object);
            session.Setup(item => item.LoadAsync<TenantProfile>(profile.Id, It.IsAny<CancellationToken>()))
                .ReturnsAsync(profile);
            session.Setup(item => item.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            Store.Setup(item => item.LightweightSession(profile.Id, It.IsAny<IsolationLevel>())).Returns(session.Object);

            Chat.Setup(item => item.TranslateNoticeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("not a notice");
            Bus.Setup(item => item.InvokeAsync(It.IsAny<ReplyGenerated>(), It.IsAny<CancellationToken>(), null))
                .Returns(Task.CompletedTask);
            Bus.Setup(item => item.InvokeAsync(It.IsAny<DeleteChatHistoryCommand>(), It.IsAny<CancellationToken>(), null))
                .Returns(Task.CompletedTask);
        }
    }
}
