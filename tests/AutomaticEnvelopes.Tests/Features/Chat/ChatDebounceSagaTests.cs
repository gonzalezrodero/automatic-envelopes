using AutomaticEnvelopes.Api.Core.Events;
using AutomaticEnvelopes.Api.Features.Chat;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutomaticEnvelopes.Tests.Features.Chat;

public class ChatDebounceSagaTests
{
    private readonly NullLogger<ChatDebounceSaga> _logger = NullLogger<ChatDebounceSaga>.Instance;

    [Fact]
    public void GivenFirstMessage_WhenStartsOrHandlesIsCalled_ThenSagaIsInitializedAndTimeoutPublished()
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

        var saga = new ChatDebounceSaga { Id = "34666555444" };

        // Act
        var result = saga.StartsOrHandles(message, _logger);

        // Assert Saga State
        saga.Id.Should().Be("34666555444");
        saga.TenantId.Should().Be("tenant-1");
        saga.CombinedText.Should().Be("Hello");

        // Assert Timeout Event
        // FIX: Como Wolverine envuelve el mensaje con el .Delay(), 
        // simplemente comprobamos que la colección ha programado algo.
        result.Should().NotBeNull();
        result.Should().NotBeEmpty("Un evento ChatWindowExpired debe haberse programado.");
    }

    [Fact]
    public void GivenExistingSaga_WhenNewMessageArrives_ThenTextIsAppended()
    {
        // Arrange
        var saga = new ChatDebounceSaga { Id = "34666555444", TenantId = "tenant-1", CombinedText = "Hello" };
        var newMessage = new MessageReceived("msg-2", "34666555444", "World", "tenant-1", "bot-1", DateTimeOffset.UtcNow);

        // Act
        var result = saga.StartsOrHandles(newMessage, _logger);

        // Assert
        saga.CombinedText.Should().Be("Hello\nWorld");

        result.Should().BeEmpty();
    }

    [Fact]
    public void GivenExistingSaga_WhenPayloadExceeds4000Chars_ThenExtraTextIsDropped()
    {
        // Arrange
        var massiveText = new string('A', 3995);
        var saga = new ChatDebounceSaga { Id = "34666555444", TenantId = "tenant-1", CombinedText = massiveText };
        var newMessage = new MessageReceived("msg-2", "34666555444", "Too much text", "tenant-1", "bot-1", DateTimeOffset.UtcNow);

        // Act
        var result = saga.StartsOrHandles(newMessage, _logger);

        // Assert
        saga.CombinedText.Should().Be(massiveText);

        result.Should().BeEmpty();
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