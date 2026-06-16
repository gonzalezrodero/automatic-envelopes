using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using AutomaticEnvelopes.Api.Features.Knowledge.Services;
using AwesomeAssertions;
using Moq;
using Moq.AutoMock;
using System.Text;

namespace AutomaticEnvelopes.Tests.Features.Knowledge.Services;

public class EmbeddingServiceTests
{
    private readonly AutoMocker mocker;
    private readonly EmbeddingService sut;

    public EmbeddingServiceTests()
    {
        mocker = new AutoMocker();
        sut = mocker.CreateInstance<EmbeddingService>();
    }

    [Fact]
    public async Task GenerateEmbeddingAsync_ReturnsFloatArray_WhenBedrockRespondsCorrectly()
    {
        // Arrange
        var textToEmbed = "Club Bàsquet Samà Vilanova";

        var jsonResponse = """
        {
            "embedding": [0.15, -0.22, 0.89],
            "inputTextTokenCount": 6,
            "message": null
        }
        """;

        var responseStream = new MemoryStream(Encoding.UTF8.GetBytes(jsonResponse));
        var invokeResponse = new InvokeModelResponse { Body = responseStream };

        mocker.GetMock<IAmazonBedrockRuntime>()
            .Setup(c => c.InvokeModelAsync(It.IsAny<InvokeModelRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(invokeResponse);

        // Act
        var result = await sut.GenerateEmbeddingAsync(textToEmbed);

        // Assert
        result.Should().NotBeNull();
        result.Should().HaveCount(3, "Porque hemos mockeado un array de 3 dimensiones en el JSON.");
        result[0].Should().Be(0.15f);
        result[1].Should().Be(-0.22f);
        result[2].Should().Be(0.89f);
    }

    [Fact]
    public async Task GenerateEmbeddingAsync_SendsCorrectRequestToTitanModel()
    {
        // Arrange
        var textToEmbed = "Testing the AWS payload";
        var responseStream = new MemoryStream(Encoding.UTF8.GetBytes("""{"embedding":[0.0]}"""));

        mocker.GetMock<IAmazonBedrockRuntime>()
            .Setup(c => c.InvokeModelAsync(It.IsAny<InvokeModelRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InvokeModelResponse { Body = responseStream });

        // Act
        await sut.GenerateEmbeddingAsync(textToEmbed);

        // Assert
        mocker.GetMock<IAmazonBedrockRuntime>()
            .Verify(c => c.InvokeModelAsync(It.Is<InvokeModelRequest>(r =>
                r.ModelId == "amazon.titan-embed-text-v2:0" &&
                r.ContentType == "application/json" &&
                r.Accept == "application/json"
            ), It.IsAny<CancellationToken>()), Times.Once, "Debe invocar a Amazon Titan V2 con los headers correctos.");
    }
}