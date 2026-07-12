using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using AutomaticEnvelopes.Api.Features.Chat;
using AutomaticEnvelopes.Api.Features.Chat.Models;
using AutomaticEnvelopes.Api.Features.Chat.Tools;
using AwesomeAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Moq;
using Moq.AutoMock;

namespace AutomaticEnvelopes.Tests.Features.Chat;

public class ChatServiceTests
{
    private readonly AutoMocker mocker;
    private readonly ChatService sut;
    private readonly BedrockSettings defaultSettings;

    public ChatServiceTests()
    {
        mocker = new AutoMocker();

        defaultSettings = new BedrockSettings
        {
            ModelId = "anthropic.claude-haiku-4-5-20251001-v1:0",
            MaxTokens = 500,
            Temperature = 0.5f
        };

        mocker.Use(Options.Create(defaultSettings));

        // Ensure the tools IEnumerable is not null
        mocker.Use<IEnumerable<IBedrockTool>>([]);

        sut = mocker.CreateInstance<ChatService>();
    }

    [Fact]
    public async Task GetResponseAsync_ReturnsParsedText_WhenBedrockRespondsCorrectly()
    {
        // Arrange
        var expectedResponseText = "¡Hola! Soy AutomaticEnvelopes y estoy listo para ayudar.";
        var systemPrompt = "Eres un asistente.";
        var history = new List<ChatMessage> { new(ChatRole.User, "Hola") };

        // Mock the ConverseResponse instead of a raw JSON string
        var converseResponse = new ConverseResponse
        {
            StopReason = StopReason.End_turn,
            Output = new ConverseOutput
            {
                Message = new Message
                {
                    Role = ConversationRole.Assistant,
                    Content = [new ContentBlock { Text = expectedResponseText }]
                }
            }
        };

        mocker.GetMock<IAmazonBedrockRuntime>()
            .Setup(c => c.ConverseAsync(It.IsAny<ConverseRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(converseResponse);

        // Act
        var result = await sut.GetResponseAsync(systemPrompt, history, "TestTenant", CancellationToken.None);

        // Assert
        result.Should().Be(expectedResponseText, "El servicio debería extraer correctamente el texto de la respuesta estructurada de Converse API.");
    }

    [Fact]
    public async Task GetResponseAsync_SendsCorrectRequestToBedrock()
    {
        // Arrange
        mocker.GetMock<IAmazonBedrockRuntime>()
            .Setup(c => c.ConverseAsync(It.IsAny<ConverseRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConverseResponse
            {
                StopReason = StopReason.End_turn,
                Output = new ConverseOutput
                {
                    Message = new Message { Role = ConversationRole.Assistant, Content = [new ContentBlock { Text = "ok" }] }
                }
            });

        var history = new List<ChatMessage> { new(ChatRole.User, "User") };

        // Act
        await sut.GetResponseAsync("System", history, "TestTenant", CancellationToken.None);

        // Assert
        mocker.GetMock<IAmazonBedrockRuntime>()
            .Verify(c => c.ConverseAsync(It.Is<ConverseRequest>(r =>
                r.ModelId == defaultSettings.ModelId &&
                r.InferenceConfig.MaxTokens == defaultSettings.MaxTokens &&
                r.InferenceConfig.Temperature == defaultSettings.Temperature
            ), It.IsAny<CancellationToken>()), Times.Once, "Debe llamar a Bedrock (Converse API) usando la configuración inyectada.");
    }

    [Fact]
    public async Task GivenRawHistory_WhenSanitizeHistoryAsyncCalled_ThenUsesPrivacyPrompt()
    {
        // Arrange
        var mockSanitizedText = "Historial limpio y anonimizado";

        mocker.GetMock<IAmazonBedrockRuntime>()
            .Setup(x => x.ConverseAsync(It.IsAny<ConverseRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConverseResponse
            {
                StopReason = StopReason.End_turn,
                Output = new ConverseOutput
                {
                    Message = new Message { Role = ConversationRole.Assistant, Content = [new ContentBlock { Text = mockSanitizedText }] }
                }
            });

        var rawHistory = "User: Hola, me llamo Daniel y mi DNI es 12345678Z";

        // Act
        var result = await sut.SanitizeHistoryAsync(rawHistory, CancellationToken.None);

        // Assert
        result.Should().Be(mockSanitizedText);

        mocker.GetMock<IAmazonBedrockRuntime>()
            .Verify(c => c.ConverseAsync(It.Is<ConverseRequest>(req =>
                VerifySanitizationPromptInjected(req) &&
                req.InferenceConfig.Temperature == 0 // We set temperature to 0 for sanitization
            ), It.IsAny<CancellationToken>()), Times.Once, "Bedrock debe ser invocado con el System Prompt de anonimización.");
    }

    [Fact]
    public async Task GetResponseAsync_InjectsToolConfig_WhenTenantHasToolsRegistered()
    {
        // Arrange
        var toolMock = new Mock<IBedrockTool>();
        toolMock.Setup(t => t.Tenant).Returns("TestTenant"); // Tenant matches!
        toolMock.Setup(t => t.GetSpecification()).Returns(new ToolSpecification { Name = "test_tool" });

        // Re-initialize AutoMocker with the populated list, then create the SUT
        mocker.Use<IEnumerable<IBedrockTool>>([toolMock.Object]);
        var sutWithTools = mocker.CreateInstance<ChatService>();

        var bedrockMock = mocker.GetMock<IAmazonBedrockRuntime>();
        bedrockMock.Setup(c => c.ConverseAsync(It.IsAny<ConverseRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConverseResponse
            {
                StopReason = StopReason.End_turn,
                Output = new ConverseOutput { Message = new Message { Role = ConversationRole.Assistant, Content = [new ContentBlock { Text = "Response" }] } }
            });

        // Act
        // Make sure we request "TestTenant" so the tool is actually loaded!
        await sutWithTools.GetResponseAsync("System", [], "TestTenant", CancellationToken.None);

        // Assert
        // Use a simpler Verify and assert the captured argument using Moq's Callback/Capture pattern for stability
        bedrockMock.Verify(c => c.ConverseAsync(It.Is<ConverseRequest>(r =>
            r.ToolConfig != null &&
            r.ToolConfig.Tools != null &&
            r.ToolConfig.Tools.Count == 1 &&
            r.ToolConfig.Tools[0].ToolSpec.Name == "test_tool" // Use array index instead of LINQ First()
        ), It.IsAny<CancellationToken>()), Times.Once, "The ToolConfig should be mapped into the ConverseRequest.");
    }

    [Fact]
    public async Task GetResponseAsync_WhenToolRequested_ExecutesToolAndReturnsFinalText()
    {
        // Arrange
        var toolMock = new Mock<IBedrockTool>();
        toolMock.Setup(t => t.Tenant).Returns("TestTenant"); // Tenant matches!
        toolMock.Setup(t => t.GetSpecification()).Returns(new ToolSpecification { Name = "test_tool" });

        // Re-initialize AutoMocker with the populated list, then create the SUT
        mocker.Use<IEnumerable<IBedrockTool>>([toolMock.Object]);
        var sutWithTools = mocker.CreateInstance<ChatService>();

        var toolExecutionServiceMock = mocker.GetMock<IToolExecutionService>();

        // Simulate the Tool Execution Service returning a mocked tool result
        var mockFollowUpMessage = new Message { Role = ConversationRole.User, Content = [] };
        toolExecutionServiceMock
            .Setup(x => x.ExecuteToolsAsync(It.IsAny<Message>(), It.IsAny<IEnumerable<IBedrockTool>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockFollowUpMessage);

        var bedrockMock = mocker.GetMock<IAmazonBedrockRuntime>();

        // Setup Sequence for the multi-turn loop
        bedrockMock.SetupSequence(c => c.ConverseAsync(It.IsAny<ConverseRequest>(), It.IsAny<CancellationToken>()))
            // 1st Call returns Tool Use
            .ReturnsAsync(new ConverseResponse
            {
                StopReason = StopReason.Tool_use,
                Output = new ConverseOutput { Message = new Message { Role = ConversationRole.Assistant, Content = [] } }
            })
            // 2nd Call returns Final Answer
            .ReturnsAsync(new ConverseResponse
            {
                StopReason = StopReason.End_turn,
                Output = new ConverseOutput { Message = new Message { Role = ConversationRole.Assistant, Content = [new ContentBlock { Text = "Final Tool Answer" }] } }
            });

        // Act
        // Must use "TestTenant" to trigger the tool loading
        var result = await sutWithTools.GetResponseAsync("System", [new ChatMessage(ChatRole.User, "Do it")], "TestTenant", CancellationToken.None);

        // Assert
        result.Should().Be("Final Tool Answer");

        // Verify it was called with ANY enumerable that isn't null. 
        // We know it worked because the mock returned the mockFollowUpMessage.
        toolExecutionServiceMock.Verify(x => x.ExecuteToolsAsync(
            It.IsAny<Message>(),
            It.IsNotNull<IEnumerable<IBedrockTool>>(),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }


    private static bool VerifySanitizationPromptInjected(ConverseRequest request)
    {
        // Converse API stores system prompts as a list of SystemContentBlocks
        var systemPrompt = string.Join(" ", request.System.Select(s => s.Text));

        return systemPrompt.Contains("You are a strict data privacy filter") &&
               systemPrompt.Contains("[NAME]"); // Comprobamos que se incluye el BotPrompts.SanitizationPrompt
    }
}