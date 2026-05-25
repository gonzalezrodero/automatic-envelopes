using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Moq.AutoMock;
using SamaBot.Api.Features.Chat;
using System.Text;

namespace SamaBot.Tests.Features.Chat;

public class ChatServiceTests
{
    private readonly AutoMocker _mocker;
    private readonly ChatService _sut;
    private readonly BedrockSettings _defaultSettings;

    public ChatServiceTests()
    {
        _mocker = new AutoMocker();

        _defaultSettings = new BedrockSettings
        {
            ModelId = "anthropic.claude-haiku-4-5-20251001-v1:0",
            MaxTokens = 500,
            Temperature = 0.5f
        };

        _mocker.Use(Options.Create(_defaultSettings));

        _sut = _mocker.CreateInstance<ChatService>();
    }

    [Fact]
    public async Task GetResponseAsync_ReturnsParsedText_WhenBedrockRespondsCorrectly()
    {
        // Arrange
        var expectedResponseText = "¡Hola! Soy SamaBot y estoy listo para ayudar.";
        var systemPrompt = "Eres un asistente.";

        // Wrap the user prompt in the new ChatMessage list format
        var history = new List<ChatMessage> { new("user", "Hola") };

        var jsonResponse = $$"""
        {
            "id": "msg_01XFDxyz",
            "type": "message",
            "role": "assistant",
            "content": [
                {
                    "type": "text",
                    "text": "{{expectedResponseText}}"
                }
            ]
        }
        """;

        var responseStream = new MemoryStream(Encoding.UTF8.GetBytes(jsonResponse));
        var invokeResponse = new InvokeModelResponse { Body = responseStream };

        _mocker.GetMock<IAmazonBedrockRuntime>()
            .Setup(c => c.InvokeModelAsync(It.IsAny<InvokeModelRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(invokeResponse);

        // Act
        var result = await _sut.GetResponseAsync(systemPrompt, history, CancellationToken.None);

        // Assert
        result.Should().Be(expectedResponseText, "El servicio debería parsear correctamente el campo 'text' del JSON devuelto por Claude 3.");
    }

    [Fact]
    public async Task GetResponseAsync_SendsCorrectRequestToBedrock()
    {
        // Arrange
        var responseStream = new MemoryStream(Encoding.UTF8.GetBytes("""{"content":[{"text":"ok"}]}"""));
        _mocker.GetMock<IAmazonBedrockRuntime>()
            .Setup(c => c.InvokeModelAsync(It.IsAny<InvokeModelRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InvokeModelResponse { Body = responseStream });

        var history = new List<ChatMessage> { new("user", "User") };

        // Act
        await _sut.GetResponseAsync("System", history, CancellationToken.None);

        // Assert
        _mocker.GetMock<IAmazonBedrockRuntime>()
            .Verify(c => c.InvokeModelAsync(It.Is<InvokeModelRequest>(r =>
                r.ModelId == _defaultSettings.ModelId &&
                r.ContentType == "application/json" &&
                r.Accept == "application/json"
            ), It.IsAny<CancellationToken>()), Times.Once, "Debe llamar a Bedrock usando la configuración inyectada.");
    }

    [Fact]
    public async Task GivenRawHistory_WhenSanitizeHistoryAsyncCalled_ThenUsesPrivacyPrompt()
    {
        // Arrange
        var mockBedrockResponse = "{\"content\":[{\"text\":\"Historial limpio y anonimizado\"}]}";

        _mocker.GetMock<IAmazonBedrockRuntime>()
            .Setup(x => x.InvokeModelAsync(It.IsAny<InvokeModelRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InvokeModelResponse
            {
                Body = new MemoryStream(Encoding.UTF8.GetBytes(mockBedrockResponse))
            });

        var rawHistory = "User: Hola, me llamo Daniel y mi DNI es 12345678Z";

        // Act
        var result = await _sut.SanitizeHistoryAsync(rawHistory, CancellationToken.None);

        // Assert
        result.Should().Be("Historial limpio y anonimizado");

        _mocker.GetMock<IAmazonBedrockRuntime>()
            .Verify(c => c.InvokeModelAsync(It.Is<InvokeModelRequest>(req =>
                VerifySanitizationPromptInjected(req)
            ), It.IsAny<CancellationToken>()), Times.Once, "Bedrock debe ser invocado con el System Prompt de anonimización.");
    }

    private static bool VerifySanitizationPromptInjected(InvokeModelRequest request)
    {
        var requestJson = Encoding.UTF8.GetString(request.Body.ToArray());
        return requestJson.Contains("You are a strict data privacy filter") &&
               requestJson.Contains("[NAME]"); // Comprobamos que se incluye el BotPrompts.SanitizationPrompt
    }
}