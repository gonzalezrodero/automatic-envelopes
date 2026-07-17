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
        var saga = new ChatDebounceSaga { Id = "34666555444" };

        var result = saga.StartsOrHandles(message, _logger);

        saga.Id.Should().Be("34666555444");
        saga.CombinedText.Should().Be("Hello");

        result.Should().NotBeNull();
        var expiredEvent = result.OfType<ChatWindowExpired>().FirstOrDefault();

        expiredEvent.Should().NotBeNull("Un evento ChatWindowExpired debe haberse encolado.");
        expiredEvent!.PhoneNumber.Should().Be("34666555444");
    }

    [Fact]
    public void GivenExistingSaga_WhenNewMessageArrives_ThenTextIsAppended()
    {
        var saga = new ChatDebounceSaga { Id = "34666555444", TenantId = "tenant-1", CombinedText = "Hello" };
        var newMessage = new MessageReceived("msg-2", "34666555444", "World", "tenant-1", "bot-1", DateTimeOffset.UtcNow);

        var result = saga.StartsOrHandles(newMessage, _logger);

        saga.CombinedText.Should().Be("Hello\nWorld");
        result.Should().BeEmpty();
    }

    [Fact]
    public void GivenExistingSaga_WhenPayloadExceeds4000Chars_ThenExtraTextIsDropped()
    {
        var massiveText = new string('A', 3995);
        var saga = new ChatDebounceSaga { Id = "34666555444", TenantId = "tenant-1", CombinedText = massiveText };
        var newMessage = new MessageReceived("msg-2", "34666555444", "Too much text", "tenant-1", "bot-1", DateTimeOffset.UtcNow);

        var result = saga.StartsOrHandles(newMessage, _logger);

        saga.CombinedText.Should().Be(massiveText);
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GivenSagaTimeout_WhenHandled_ThenAICommandIsDispatchedAndSagaCompletes()
    {
        var saga = new ChatDebounceSaga { Id = "34666555444", TenantId = "tenant-1", BotPhoneNumberId = "bot-1", CombinedText = "Hello\nWorld" };
        var timeoutEvent = new ChatWindowExpired("34666555444");

        var busMock = new Mock<IMessageBus>();

        await saga.Handle(timeoutEvent, busMock.Object, _logger);

        busMock.Verify(b => b.InvokeAsync(
            It.Is<AnalyzeChatSession>(c =>
                c.PhoneNumber == "34666555444" &&
                c.CombinedText == "Hello\nWorld"),
            It.IsAny<CancellationToken>(),
            null), Times.Once);

        saga.IsCompleted().Should().BeTrue();
    }
}