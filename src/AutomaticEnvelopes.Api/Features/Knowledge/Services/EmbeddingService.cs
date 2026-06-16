using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using System.Text.Json;

namespace AutomaticEnvelopes.Api.Features.Knowledge.Services;

public interface IEmbeddingService
{
    Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct = default);
}

public class EmbeddingService(IAmazonBedrockRuntime client) : IEmbeddingService
{
    private static readonly JsonSerializerOptions options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private const string TitanEmbeddingsModelId = "amazon.titan-embed-text-v2:0";

    public async Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct = default)
    {
        var payload = new
        {
            inputText = text,
            dimensions = 512,
            normalize = true
        };

        var request = new InvokeModelRequest
        {
            ModelId = TitanEmbeddingsModelId,
            ContentType = "application/json",
            Accept = "application/json",
            Body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(payload))
        };

        var response = await client.InvokeModelAsync(request, ct);

        using var reader = new StreamReader(response.Body);
        var responseBody = await reader.ReadToEndAsync(ct);

        var result = JsonDocument.Parse(responseBody);

        var embeddingArray = result.RootElement
            .GetProperty("embedding")
            .Deserialize<float[]>(options);

        return embeddingArray ?? [];
    }
}