using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using AwesomeAssertions;
using Moq;
using SamaBot.Api.Features.Chat;
using SamaBot.Api.Features.Chat.Tools;
using SamaBot.Api.Features.Tenants.Helpers;
using System.Text.Json;

namespace SamaBot.Tests.Features.Chat.Tools;

public class ToolExecutionServiceTests
{
    private readonly ToolExecutionService _sut = new();

    [Fact]
    public async Task ExecuteToolsAsync_ReturnsNull_WhenNoToolUseBlockExists()
    {
        // Arrange
        var message = new Message
        {
            Role = ConversationRole.Assistant,
            Content = [new ContentBlock { Text = "Just a normal text response" }]
        };

        // Act
        var result = await _sut.ExecuteToolsAsync(message, [], CancellationToken.None);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteToolsAsync_ReturnsNull_WhenRequestedToolNotFound()
    {
        // Arrange
        var emptyInput = JsonConverter.ToAwsDocument(JsonDocument.Parse("{}").RootElement);

        var message = new Message
        {
            Role = ConversationRole.Assistant,
            Content = [new ContentBlock { ToolUse = new ToolUseBlock { Name = "non_existent_tool", ToolUseId = "123", Input = emptyInput } }]
        };

        var availableToolMock = new Mock<IBedrockTool>();
        availableToolMock.Setup(t => t.GetSpecification()).Returns(new ToolSpecification { Name = "different_tool" });

        // Act
        var result = await _sut.ExecuteToolsAsync(message, [availableToolMock.Object], CancellationToken.None);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteToolsAsync_ExecutesToolAndReturnsMessage_WhenToolMatches()
    {
        // Arrange
        var inputDocument = JsonConverter.ToAwsDocument(JsonDocument.Parse("{\"key\":\"value\"}").RootElement);

        var message = new Message
        {
            Role = ConversationRole.Assistant,
            Content = [new ContentBlock { ToolUse = new ToolUseBlock { Name = "my_tool", ToolUseId = "tool_abc", Input = inputDocument } }]
        };

        var toolMock = new Mock<IBedrockTool>();
        toolMock.Setup(t => t.GetSpecification()).Returns(new ToolSpecification { Name = "my_tool" });
        toolMock.Setup(t => t.ExecuteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("{\"success\": true}");

        // Act
        var result = await _sut.ExecuteToolsAsync(message, [toolMock.Object], CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Role.Should().Be(ConversationRole.User);
        result.Content.Should().ContainSingle();

        var toolResultBlock = result.Content.First().ToolResult;
        toolResultBlock.Should().NotBeNull();
        toolResultBlock.ToolUseId.Should().Be("tool_abc");
        toolResultBlock.Content.First().Text.Should().Be("{\"success\": true}");
    }
}