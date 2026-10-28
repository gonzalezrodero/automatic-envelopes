using System.Data;
using AutomaticEnvelopes.Api.Common;
using AutomaticEnvelopes.Api.Core.Events;
using AutomaticEnvelopes.Api.Features.Chat;
using AwesomeAssertions;
using Marten;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Wolverine;

namespace AutomaticEnvelopes.Tests.Features.Chat;

public class ChatDebounceSagaTests
{
    private readonly NullLogger<ChatDebounceSaga> _logger = NullLogger<ChatDebounceSaga>.Instance;

    [Fact]
    public async Task GivenFirstMessage_WhenStartsOrHandlesIsCalled_ThenSagaIsInitializedAndTimeoutPublished()
    {
        var message = new MessageReceived("msg-1", "34666555444", "Hello", "tenant-1", "bot-1", DateTimeOffset.UtcNow);
        var saga = new ChatDebounceSaga();
        var session = new Mock<IDocumentSession>();

        var result = await saga.StartsOrHandles(message, session.Object, IdleStore(), _logger, CancellationToken.None);

        saga.Id.Should().Be(ChatSagaIds.For("tenant-1", "34666555444"));
        saga.PhoneNumber.Should().Be("34666555444");
        saga.TenantId.Should().Be("tenant-1");
        saga.CombinedText.Should().Be("Hello");
        saga.StartedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));

        result.Should().NotBeNull();
        var expiredEvent = result.OfType<ChatWindowExpired>().FirstOrDefault();

        expiredEvent.Should().NotBeNull("Un evento ChatWindowExpired debe haberse encolado.");
        expiredEvent!.SagaId.Should().Be(saga.Id);
        expiredEvent.WindowStartedAtTicks.Should().Be(saga.StartedAt.UtcTicks);
        session.Verify(s => s.Delete<ChatDebounceMarker>(saga.Id), Times.Once);
    }

    [Fact]
    public async Task GivenExistingSaga_WhenNewMessageArrives_ThenTextIsAppended()
    {
        var started = DateTimeOffset.UtcNow;
        var saga = new ChatDebounceSaga
        {
            Id = ChatSagaIds.For("tenant-1", "34666555444"),
            PhoneNumber = "34666555444",
            TenantId = "tenant-1",
            CombinedText = "Hello",
            StartedAt = started
        };
        var newMessage = new MessageReceived("msg-2", "34666555444", "World", "tenant-1", "bot-1", DateTimeOffset.UtcNow);

        var result = await saga.StartsOrHandles(newMessage, new Mock<IDocumentSession>().Object, IdleStore(), _logger, CancellationToken.None);

        saga.CombinedText.Should().Be("Hello\nWorld");
        saga.StartedAt.Should().Be(started);
        result.OfType<ChatWindowExpired>().Should().BeEmpty();
    }

    [Fact]
    public async Task GivenExistingSaga_WhenPayloadExceeds4000Chars_ThenExtraTextIsDropped()
    {
        var massiveText = new string('A', 3995);
        var saga = new ChatDebounceSaga
        {
            Id = ChatSagaIds.For("tenant-1", "34666555444"),
            PhoneNumber = "34666555444",
            TenantId = "tenant-1",
            CombinedText = massiveText,
            StartedAt = DateTimeOffset.UtcNow
        };
        var newMessage = new MessageReceived("msg-2", "34666555444", "Too much text", "tenant-1", "bot-1", DateTimeOffset.UtcNow);

        var result = await saga.StartsOrHandles(newMessage, new Mock<IDocumentSession>().Object, IdleStore(), _logger, CancellationToken.None);

        saga.CombinedText.Should().Be(massiveText);
        result.OfType<ChatWindowExpired>().Should().BeEmpty();
    }

    [Fact]
    public async Task GivenOverdueSaga_WhenAnotherMessageArrives_ThenExpiryIsRepublishedOnce()
    {
        var saga = new ChatDebounceSaga
        {
            Id = ChatSagaIds.For("tenant-1", "34666555444"),
            PhoneNumber = "34666555444",
            TenantId = "tenant-1",
            CombinedText = "Hello",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-30)
        };
        var again = new MessageReceived("msg-2", "34666555444", "Still here", "tenant-1", "bot-1", DateTimeOffset.UtcNow);

        var first = await saga.StartsOrHandles(again, new Mock<IDocumentSession>().Object, IdleStore(), _logger, CancellationToken.None);
        var second = await saga.StartsOrHandles(
            again with { MessageId = "msg-3", Text = "And again" },
            new Mock<IDocumentSession>().Object,
            IdleStore(),
            _logger,
            CancellationToken.None);

        first.OfType<ChatWindowExpired>().Should().ContainSingle(expired =>
            expired.SagaId == saga.Id && expired.WindowStartedAtTicks == saga.StartedAt.UtcTicks);
        saga.CombinedText.Should().Be("Hello\nStill here\nAnd again");
        second.OfType<ChatWindowExpired>().Should().BeEmpty();
    }

    [Fact]
    public async Task GivenSagaTimeout_WhenHandled_ThenAICommandIsDispatchedAndSagaCompletes()
    {
        var saga = OpenSaga();
        var session = SessionWith(saga, marker: null);

        var result = await ChatWindowExpiredHandler.Handle(
            new ChatWindowExpired(saga.Id, saga.StartedAt.UtcTicks),
            session.Object,
            NullLogger<ChatWindowExpired>.Instance,
            CancellationToken.None);

        var command = result.OfType<AnalyzeChatSession>().Should().ContainSingle().Subject;
        command.PhoneNumber.Should().Be("34666555444");
        command.TenantId.Should().Be("tenant-1");
        command.CombinedText.Should().Be("Hello\nWorld");
        session.Verify(s => s.Insert(It.Is<ChatDebounceMarker>(marker => marker.Id == saga.Id)), Times.Once);
        session.Verify(s => s.Delete(saga), Times.Once);
    }

    [Fact]
    public async Task GivenAClosedWindow_WhenTheSameExpiryArrivesAgain_ThenNoSecondReplyIsDispatched()
    {
        var saga = OpenSaga();
        var session = SessionWith(saga, new ChatDebounceMarker { Id = saga.Id });

        var result = await ChatWindowExpiredHandler.Handle(
            new ChatWindowExpired(saga.Id, saga.StartedAt.UtcTicks),
            session.Object,
            NullLogger<ChatWindowExpired>.Instance,
            CancellationToken.None);

        result.OfType<AnalyzeChatSession>().Should().BeEmpty();
        session.Verify(s => s.Insert(It.IsAny<ChatDebounceMarker>()), Times.Never);
        session.Verify(s => s.Delete(saga), Times.Once);
    }

    [Fact]
    public async Task GivenANewerWindow_WhenAnOlderExpiryArrives_ThenTheSagaStaysOpen()
    {
        var saga = OpenSaga();
        var session = SessionWith(saga, marker: null);

        var result = await ChatWindowExpiredHandler.Handle(
            new ChatWindowExpired(saga.Id, saga.StartedAt.UtcTicks - 1),
            session.Object,
            NullLogger<ChatWindowExpired>.Instance,
            CancellationToken.None);

        result.OfType<AnalyzeChatSession>().Should().BeEmpty();
        session.Verify(s => s.Delete(It.IsAny<ChatDebounceSaga>()), Times.Never);
        session.Verify(s => s.LoadAsync<ChatDebounceMarker>(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static ChatDebounceSaga OpenSaga() => new()
    {
        Id = ChatSagaIds.For("tenant-1", "34666555444"),
        PhoneNumber = "34666555444",
        TenantId = "tenant-1",
        BotPhoneNumberId = "bot-1",
        CombinedText = "Hello\nWorld",
        StartedAt = DateTimeOffset.UtcNow
    };

    private static Mock<IDocumentSession> SessionWith(ChatDebounceSaga saga, ChatDebounceMarker? marker)
    {
        var session = new Mock<IDocumentSession>();
        session.Setup(s => s.LoadAsync<ChatDebounceSaga>(saga.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(saga);
        session.Setup(s => s.LoadAsync<ChatDebounceMarker>(saga.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(marker);
        return session;
    }

    [Fact]
    public async Task StartsOrHandles_DoesNotLogTheRawPhone()
    {
        const string phone = "34666555444";
        var logger = new CaptureLogger();

        await new ChatDebounceSaga().StartsOrHandles(
            new MessageReceived("msg-1", phone, "Hello", "tenant-1", "bot-1", DateTimeOffset.UtcNow),
            new Mock<IDocumentSession>().Object,
            IdleStore(),
            logger,
            CancellationToken.None);

        var written = string.Join('\n', logger.Lines);
        written.Should().NotContain(phone);
        written.Should().Contain(LogMask.Phone(phone));
    }

    private static IDocumentStore IdleStore()
    {
        var session = new Mock<IDocumentSession>();
        session.Setup(s => s.LoadAsync<ChatDebounceMarker>(ChatDebounceMarker.RecoveryId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatDebounceMarker
            {
                Id = ChatDebounceMarker.RecoveryId,
                NotBefore = DateTimeOffset.UtcNow.AddHours(1)
            });

        var store = new Mock<IDocumentStore>();
        store.Setup(s => s.LightweightSession(It.IsAny<IsolationLevel>())).Returns(session.Object);
        return store.Object;
    }

    private sealed class CaptureLogger : ILogger<ChatDebounceSaga>
    {
        public List<string> Lines { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}