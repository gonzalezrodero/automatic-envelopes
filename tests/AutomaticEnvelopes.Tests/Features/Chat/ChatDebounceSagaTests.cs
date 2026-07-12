using AutomaticEnvelopes.Api.Core.Events;
using AutomaticEnvelopes.Api.Features.Chat;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutomaticEnvelopes.Tests.Features.Chat;

public class ChatDebounceSagaTests
{
    private readonly NullLogger<ChatDebounceSaga> _logger = NullLogger<ChatDebounceSaga>.Instance;

    [Fact]
    public void GivenFirstMessage_WhenStartsIsCalled_ThenSagaIsInitializedAndTimeoutScheduled()
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

        var saga = new ChatDebounceSaga();

        // Act - Call the instance method
        var outgoing = saga.Starts(message, _logger);

        // Assert Saga State
        saga.Id.Should().Be("34666555444");
        saga.TenantId.Should().Be("tenant-1");
        saga.CombinedText.Should().Be("Hello");

        // Assert Scheduled Timeout
        var scheduledMessage = outgoing.FirstOrDefault();
        scheduledMessage.Should().NotBeNull("A timeout message must be scheduled to close the saga.");
    }

    [Fact]
    public void GivenExistingSaga_WhenNewMessageArrives_ThenTextIsAppended()
    {
        // Arrange
        var saga = new ChatDebounceSaga
        {
            Id = "34666555444",
            CombinedText = "Hello"
        };
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
        var saga = new ChatDebounceSaga
        {
            Id = "34666555444",
            CombinedText = massiveText
        };
        var newMessage = new MessageReceived("msg-2", "34666555444", "This is too much text", "tenant-1", "bot-1", DateTimeOffset.UtcNow);

        // Act
        saga.Handle(newMessage, _logger);

        // Assert
        saga.CombinedText.Should().Be(massiveText, "The new text should be ignored because appending it crosses the 4000 character threshold.");
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
        var outgoing = saga.Handle(timeoutEvent, _logger);

        // Assert Dispatch
        var analyzeCommand = outgoing.OfType<AnalyzeChatSession>().SingleOrDefault();

        analyzeCommand.Should().NotBeNull();
        analyzeCommand!.PhoneNumber.Should().Be("34666555444");
        analyzeCommand.CombinedText.Should().Be("Hello\nWorld");

        // Assert Saga Completion
        saga.IsCompleted().Should().BeTrue("The saga must mark itself as completed so Marten deletes it from the database.");
    }
}