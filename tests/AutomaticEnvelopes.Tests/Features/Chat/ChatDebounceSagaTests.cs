using AutomaticEnvelopes.Api.Core.Events;
using AutomaticEnvelopes.Api.Features.Chat;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Wolverine;

namespace AutomaticEnvelopes.Testss.Features.Chat;

public class ChatDebounceSagaTests
{
    private readonly NullLogger<ChatDebounceSaga> _logger = NullLogger<ChatDebounceSaga>.Instance;
    private readonly Mock<IMessageBus> _busMock = new();

    [Fact]
    public async Task GivenFirstMessage_WhenStartsIsCalled_ThenSagaIsInitializedAndTimeoutPublished()
    {
        // Arrange
        var message = new MessageReceived(
            MessageId: "msg-1",
            PhoneNumber: "34666555444",
            Text: "Hello",
            TenantId: "tenant-1",
            BotPhoneNumberId: "bot-1",
            ReceivedAt: DateTimeOffset.UtcNow
        );

        // Act
        var saga = await ChatDebounceSaga.StartsAsync(message, _busMock.Object, _logger);

        // Assert Saga State
        saga.Should().NotBeNull();
        saga.Id.Should().Be("34666555444");
        saga.TenantId.Should().Be("tenant-1");
        saga.CombinedText.Should().Be("Hello");

        // Assert Timeout Event Published
        _busMock.Verify(b => b.PublishAsync(
            It.Is<ChatWindowExpired>(e => e.PhoneNumber == "34666555444"),
            default), Times.Once);
    }

    [Fact]
    public void GivenExistingSaga_WhenNewMessageArrives_ThenTextIsAppended()
    {
        // Arrange
        var saga = new ChatDebounceSaga { Id = "34666555444", CombinedText = "Hello" };
        var newMessage = new MessageReceived("msg-2", "34666555444", "World", "tenant-1", "bot-1", DateTimeOffset.UtcNow);

        // Act
        saga.Handle(newMessage, _logger);

        // Assert
        saga.CombinedText.Should().Be("Hello\nWorld");
    }

    [Fact]
    public void GivenExistingSaga_WhenPayloadExceeds4000Chars_ThenExtraTextIsDroppedToProtectCosts()
    {
        // Arrange
        var massiveText = new string('A', 3995);
        var saga = new ChatDebounceSaga { Id = "34666555444", CombinedText = massiveText };
        var newMessage = new MessageReceived("msg-2", "34666555444", "This is too much text", "tenant-1", "bot-1", DateTimeOffset.UtcNow);

        // Act
        saga.Handle(newMessage, _logger);

        // Assert
        saga.CombinedText.Should().Be(massiveText, "The new text should be ignored.");
    }

    [Fact]
    public void GivenSagaTimeout_WhenHandled_ThenAICommandIsDispatchedAndSagaCompletes()
    {
        // Arrange
        var saga = new ChatDebounceSaga
        {
            Id = "34666555444",
            TenantId = "tenant-1",
            BotPhoneNumberId = "bot-1",
            CombinedText = "Hello\nWorld"
        };
        var timeoutEvent = new ChatWindowExpired("34666555444");

        // Act
        var analyzeCommand = saga.Handle(timeoutEvent, _logger);

        // Assert Dispatch
        analyzeCommand.Should().NotBeNull();
        analyzeCommand.PhoneNumber.Should().Be("34666555444");
        analyzeCommand.CombinedText.Should().Be("Hello\nWorld");

        // Assert Saga Completion
        saga.IsCompleted().Should().BeTrue();
    }
}