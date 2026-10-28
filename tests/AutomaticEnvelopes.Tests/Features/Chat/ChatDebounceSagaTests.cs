using AutomaticEnvelopes.Api.Common;
using AutomaticEnvelopes.Api.Core.Events;
using AutomaticEnvelopes.Api.Features.Chat;
using AwesomeAssertions;
using JasperFx;
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
        var session = HeldLease();

        var result = await saga.StartsOrHandles(message, session.Object, _logger, CancellationToken.None);

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

        var result = await saga.StartsOrHandles(newMessage, HeldLease().Object, _logger, CancellationToken.None);

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

        var result = await saga.StartsOrHandles(newMessage, HeldLease().Object, _logger, CancellationToken.None);

        saga.CombinedText.Should().Be(massiveText);
        result.OfType<ChatWindowExpired>().Should().BeEmpty();
    }

    [Fact]
    public async Task GivenOverdueSaga_WhenTheLeaseIsHeld_ThenTheMessageOnlyAppends()
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

        var first = await saga.StartsOrHandles(again, HeldLease().Object, _logger, CancellationToken.None);
        var second = await saga.StartsOrHandles(
            again with { MessageId = "msg-3", Text = "And again" },
            HeldLease().Object,
            _logger,
            CancellationToken.None);

        first.OfType<ChatWindowExpired>().Should().BeEmpty();
        second.OfType<ChatWindowExpired>().Should().BeEmpty();
        saga.CombinedText.Should().Be("Hello\nStill here\nAnd again");
    }

    [Fact]
    public async Task GivenSagaTimeout_WhenTheReplySucceeds_ThenTheWindowIsClaimedAfterTheReply()
    {
        var saga = OpenSaga();
        var order = new List<string>();
        var session = SessionWith(saga, marker: null, order);
        var bus = new Mock<IMessageBus>();
        bus.Setup(b => b.InvokeAsync(It.IsAny<object>(), It.IsAny<CancellationToken>(), It.IsAny<TimeSpan?>()))
            .Callback(() => order.Add("ai"))
            .Returns(Task.CompletedTask);
        session.Setup(s => s.Insert(It.IsAny<ChatDebounceMarker>()))
            .Callback(() => order.Add("claim"));

        await ChatWindowExpiredHandler.Handle(
            new ChatWindowExpired(saga.Id, saga.StartedAt.UtcTicks),
            session.Object,
            bus.Object,
            CancellationToken.None);

        order.Should().Equal("ai", "delete", "claim");
        session.Verify(s => s.Eject(saga), Times.Once);
    }

    [Fact]
    public async Task GivenAClosedWindow_WhenTheSameExpiryArrivesAgain_ThenNoSecondReplyIsDispatched()
    {
        var saga = OpenSaga();
        var session = SessionWith(saga, new ChatDebounceMarker { Id = saga.Id, ClaimedVersion = saga.Version });
        var bus = new Mock<IMessageBus>();

        await ChatWindowExpiredHandler.Handle(
            new ChatWindowExpired(saga.Id, saga.StartedAt.UtcTicks),
            session.Object,
            bus.Object,
            CancellationToken.None);

        bus.Verify(b => b.InvokeAsync(It.IsAny<object>(), It.IsAny<CancellationToken>(), It.IsAny<TimeSpan?>()), Times.Never);
        session.Verify(s => s.Insert(It.IsAny<ChatDebounceMarker>()), Times.Never);
    }

    [Fact]
    public async Task GivenANewerRevision_WhenTheMarkerIsStale_ThenTheReplyRunsAgain()
    {
        var saga = OpenSaga();
        var session = SessionWith(saga, new ChatDebounceMarker { Id = saga.Id, ClaimedVersion = saga.Version - 1 });
        var bus = new Mock<IMessageBus>();
        bus.Setup(b => b.InvokeAsync(It.IsAny<object>(), It.IsAny<CancellationToken>(), It.IsAny<TimeSpan?>()))
            .Returns(Task.CompletedTask);

        await ChatWindowExpiredHandler.Handle(
            new ChatWindowExpired(saga.Id, saga.StartedAt.UtcTicks),
            session.Object,
            bus.Object,
            CancellationToken.None);

        bus.Verify(b => b.InvokeAsync(It.IsAny<object>(), It.IsAny<CancellationToken>(), It.IsAny<TimeSpan?>()), Times.Once);
        session.Verify(s => s.Delete(It.IsAny<ChatDebounceMarker>()), Times.Once);
    }

    [Fact]
    public async Task GivenTheReplyFails_WhenTheExpiryIsHandled_ThenTheSagaIsNotClaimed()
    {
        var saga = OpenSaga();
        var session = SessionWith(saga, marker: null);
        var bus = new Mock<IMessageBus>();
        bus.Setup(b => b.InvokeAsync(It.IsAny<object>(), It.IsAny<CancellationToken>(), It.IsAny<TimeSpan?>()))
            .ThrowsAsync(new ConcurrencyException("bedrock"));

        var act = () => ChatWindowExpiredHandler.Handle(
            new ChatWindowExpired(saga.Id, saga.StartedAt.UtcTicks),
            session.Object,
            bus.Object,
            CancellationToken.None);

        await act.Should().ThrowAsync<ConcurrencyException>();
        session.Verify(s => s.Insert(It.IsAny<ChatDebounceMarker>()), Times.Never);
        session.Verify(s => s.Eject(It.IsAny<ChatDebounceSaga>()), Times.Never);
    }

    [Fact]
    public async Task GivenANewerWindow_WhenAnOlderExpiryArrives_ThenTheSagaStaysOpen()
    {
        var saga = OpenSaga();
        var session = SessionWith(saga, marker: null);
        var bus = new Mock<IMessageBus>();

        await ChatWindowExpiredHandler.Handle(
            new ChatWindowExpired(saga.Id, saga.StartedAt.UtcTicks - 1),
            session.Object,
            bus.Object,
            CancellationToken.None);

        bus.Verify(b => b.InvokeAsync(It.IsAny<object>(), It.IsAny<CancellationToken>(), It.IsAny<TimeSpan?>()), Times.Never);
        session.Verify(s => s.Insert(It.IsAny<ChatDebounceMarker>()), Times.Never);
        session.Verify(s => s.Eject(It.IsAny<ChatDebounceSaga>()), Times.Never);
    }

    [Fact]
    public async Task StartsOrHandles_DoesNotLogTheRawPhone()
    {
        const string phone = "34666555444";
        var logger = new CaptureLogger();

        await new ChatDebounceSaga().StartsOrHandles(
            new MessageReceived("msg-1", phone, "Hello", "tenant-1", "bot-1", DateTimeOffset.UtcNow),
            HeldLease().Object,
            logger,
            CancellationToken.None);

        var written = string.Join('\n', logger.Lines);
        written.Should().NotContain(phone);
        written.Should().Contain(LogMask.Phone(phone));
    }

    private static ChatDebounceSaga OpenSaga() => new()
    {
        Id = ChatSagaIds.For("tenant-1", "34666555444"),
        PhoneNumber = "34666555444",
        TenantId = "tenant-1",
        BotPhoneNumberId = "bot-1",
        CombinedText = "Hello\nWorld",
        StartedAt = DateTimeOffset.UtcNow,
        Version = 4
    };

    private static Mock<IDocumentSession> HeldLease()
    {
        var session = new Mock<IDocumentSession>();
        session.Setup(s => s.LoadAsync<ChatDebounceMarker>(ChatDebounceMarker.RecoveryId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatDebounceMarker
            {
                Id = ChatDebounceMarker.RecoveryId,
                NotBefore = DateTimeOffset.UtcNow.AddHours(1)
            });
        return session;
    }

    private static Mock<IDocumentSession> SessionWith(ChatDebounceSaga saga, ChatDebounceMarker? marker, List<string>? order = null)
    {
        var session = new Mock<IDocumentSession>();
        var sql = new Mock<IAdvancedSql>();
        sql.Setup(s => s.QueryAsync<long>(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<object[]>()))
            .ReturnsAsync(new List<long> { saga.Version });
        sql.Setup(s => s.QueryAsync<string>(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<object[]>()))
            .Callback(() => order?.Add("delete"))
            .ReturnsAsync(new List<string> { saga.Id });
        session.Setup(s => s.AdvancedSql).Returns(sql.Object);
        session.Setup(s => s.BeginTransactionAsync(It.IsAny<CancellationToken>())).Returns(ValueTask.CompletedTask);
        session.Setup(s => s.LoadAsync<ChatDebounceSaga>(saga.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(saga);
        session.Setup(s => s.LoadAsync<ChatDebounceMarker>(saga.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(marker);
        return session;
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
