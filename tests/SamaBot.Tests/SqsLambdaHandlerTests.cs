using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using Moq;
using Moq.AutoMock;
using SamaBot.Api;
using SamaBot.Api.Features.WhatsAppWebhook;
using System.Text.Json;
using Wolverine;

namespace SamaBot.Tests;

[Collection("Integration")]
public class SqsLambdaHandlerTests
{
    private readonly Mock<IMessageBus> busMock = new();
    private readonly Mock<ILambdaContext> contextMock = new();
    private readonly SqsLambdaHandler handler = new();

    [Fact]
    public async Task FunctionHandler_ShouldInvokeBus_ForEachSqsRecord()
    {
        // Arrange
        var mocker = new AutoMocker();
        var handler = mocker.CreateInstance<SqsLambdaHandler>();

        var expectedMessage = new ProcessWhatsAppMessage("123", "34600000000", "34600000000", "Text", DateTimeOffset.UtcNow);
        var jsonBody = JsonSerializer.Serialize(expectedMessage);

        var sqsEvent = new SQSEvent
        {
            Records =
            [
                new() { Body = jsonBody }
            ]
        };

        var lambdaContext = mocker.GetMock<ILambdaContext>().Object;

        // Act
        await handler.FunctionHandler(sqsEvent, lambdaContext);

        // Assert
        mocker.GetMock<IMessageBus>()
            .Verify(b => b.InvokeAsync(
                It.Is<ProcessWhatsAppMessage>(m =>
                    m.MessageId == expectedMessage.MessageId &&
                    m.Text == expectedMessage.Text),
                It.IsAny<CancellationToken>(),
                It.IsAny<TimeSpan?>()),
            Times.Once);
    }

    [Fact]
    public async Task FunctionHandler_WithEmptyRecords_ShouldNotInvokeBus()
    {
        // Arrange
        var sqsEvent = new SQSEvent { Records = [] };

        // Act
        await handler.FunctionHandler(sqsEvent, contextMock.Object);

        // Assert
        busMock.Verify(x => x.InvokeAsync(It.IsAny<object>(), default, null), Times.Never);
    }
}