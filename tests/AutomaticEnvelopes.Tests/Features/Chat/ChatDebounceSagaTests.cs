using System.Text.Json;
using AutomaticEnvelopes.Api.Core.Events;
using AutomaticEnvelopes.Api.Features.Chat;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Wolverine;

namespace AutomaticEnvelopes.Tests.Features.Chat;

public class ChatDebounceSagaTests
{
    private readonly NullLogger<ChatDebounceSaga> _logger = NullLogger<ChatDebounceSaga>.Instance;

    [Fact]
    public void GivenFirstMessage_WhenStartsOrHandlesIsCalled_ThenSagaIsInitializedAndTimeoutPublished()
    {
        var message = new MessageReceived("msg-1", "34666555444", "Hello", "tenant-1", "bot-1", DateTimeOffset.UtcNow);
        var saga = new ChatDebounceSaga();

        var result = saga.StartsOrHandles(message, _logger);

        saga.Id.Should().Be(ChatSagaIds.For("tenant-1", "34666555444"));
        saga.PhoneNumber.Should().Be("34666555444");
        saga.TenantId.Should().Be("tenant-1");
        saga.CombinedText.Should().Be("Hello");

        result.Should().NotBeNull();
        var expiredEvent = result.OfType<ChatWindowExpired>().FirstOrDefault();

        expiredEvent.Should().NotBeNull("Un evento ChatWindowExpired debe haberse encolado.");
        expiredEvent!.PhoneNumber.Should().Be(saga.Id);
    }

    [Fact]
    public void GivenExistingSaga_WhenNewMessageArrives_ThenTextIsAppended()
    {
        var saga = new ChatDebounceSaga
        {
            Id = ChatSagaIds.For("tenant-1", "34666555444"),
            PhoneNumber = "34666555444",
            TenantId = "tenant-1",
            CombinedText = "Hello"
        };
        var newMessage = new MessageReceived("msg-2", "34666555444", "World", "tenant-1", "bot-1", DateTimeOffset.UtcNow);

        var result = saga.StartsOrHandles(newMessage, _logger);

        saga.CombinedText.Should().Be("Hello\nWorld");
        result.Should().BeEmpty();
    }

    [Fact]
    public void GivenExistingSaga_WhenPayloadExceeds4000Chars_ThenExtraTextIsDropped()
    {
        var massiveText = new string('A', 3995);
        var saga = new ChatDebounceSaga
        {
            Id = ChatSagaIds.For("tenant-1", "34666555444"),
            PhoneNumber = "34666555444",
            TenantId = "tenant-1",
            CombinedText = massiveText
        };
        var newMessage = new MessageReceived("msg-2", "34666555444", "Too much text", "tenant-1", "bot-1", DateTimeOffset.UtcNow);

        var result = saga.StartsOrHandles(newMessage, _logger);

        saga.CombinedText.Should().Be(massiveText);
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GivenSagaTimeout_WhenHandled_ThenAICommandIsDispatchedAndSagaCompletes()
    {
        var saga = new ChatDebounceSaga
        {
            Id = ChatSagaIds.For("tenant-1", "34666555444"),
            PhoneNumber = "34666555444",
            TenantId = "tenant-1",
            BotPhoneNumberId = "bot-1",
            CombinedText = "Hello\nWorld"
        };
        var timeoutEvent = new ChatWindowExpired(saga.Id);

        var busMock = new Mock<IMessageBus>();

        await saga.Handle(timeoutEvent, busMock.Object, _logger);

        busMock.Verify(b => b.InvokeAsync(
            It.Is<AnalyzeChatSession>(c =>
                c.PhoneNumber == "34666555444" &&
                c.TenantId == "tenant-1" &&
                c.CombinedText == "Hello\nWorld"),
            It.IsAny<CancellationToken>(),
            null), Times.Once);

        saga.IsCompleted().Should().BeTrue();
    }

    [Fact]
    public async Task GivenLegacyPhoneKeyedSaga_WhenWindowExpires_ThenDispatchUsesTheStoredPhone()
    {
        var saga = new ChatDebounceSaga
        {
            Id = "34666555444",
            TenantId = "tenant-1",
            BotPhoneNumberId = "bot-1",
            CombinedText = "Already queued"
        };

        var busMock = new Mock<IMessageBus>();

        await saga.Handle(new ChatWindowExpired("34666555444"), busMock.Object, _logger);

        busMock.Verify(b => b.InvokeAsync(
            It.Is<AnalyzeChatSession>(c =>
                c.PhoneNumber == "34666555444" &&
                c.TenantId == "tenant-1" &&
                c.CombinedText == "Already queued"),
            It.IsAny<CancellationToken>(),
            null), Times.Once);
    }

    [Fact]
    public void SagaIdentity_IncludesTenant_AndLegacyPayloadStillDeserializes()
    {
        var samePhone = "34666555444";
        var first = new MessageReceived("msg-1", samePhone, "Hello", "tenant-a", "bot-a", DateTimeOffset.UtcNow);
        var second = new MessageReceived("msg-2", samePhone, "Hello", "tenant-b", "bot-b", DateTimeOffset.UtcNow);

        first.SagaId.Should().Be(ChatSagaIds.For("tenant-a", samePhone));
        second.SagaId.Should().Be(ChatSagaIds.For("tenant-b", samePhone));
        first.SagaId.Should().NotBe(second.SagaId);
        ChatDebounceSaga.Identity(first).Should().Be(first.SagaId);

        var queued = JsonSerializer.Serialize(new ChatWindowExpired(first.SagaId));
        queued.Should().Contain("\"PhoneNumber\"");
        queued.Should().NotContain("\"SagaId\"");

        var legacy = JsonSerializer.Deserialize<ChatWindowExpired>("""{"PhoneNumber":"34666555444"}""");
        legacy.Should().NotBeNull();
        legacy!.PhoneNumber.Should().Be("34666555444");
    }
}