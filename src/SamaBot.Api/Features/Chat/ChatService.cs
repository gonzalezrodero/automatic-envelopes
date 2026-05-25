using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Microsoft.Extensions.Options;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace SamaBot.Api.Features.Chat;

public interface IChatService
{
    Task<string> GetResponseAsync(string systemPrompt, List<ChatMessage> history, CancellationToken ct);
    Task<string> SanitizeHistoryAsync(string rawHistory, CancellationToken ct);
}

public class ChatService(IAmazonBedrockRuntime client, IOptions<BedrockSettings> settings) : IChatService
{
    private readonly BedrockSettings settings = settings.Value;

    private static readonly JsonSerializerOptions jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public async Task<string> GetResponseAsync(string systemPrompt, List<ChatMessage> history, CancellationToken ct)
    {
        var payload = new
        {
            anthropic_version = "bedrock-2023-05-31",
            max_tokens = settings.MaxTokens,
            temperature = settings.Temperature,
            system = systemPrompt,
            messages = FormatChatMessages(history)
        };

        var request = new InvokeModelRequest
        {
            ModelId = settings.ModelId,
            ContentType = "application/json",
            Accept = "application/json",
            Body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(payload, jsonOptions))
        };

        var response = await client.InvokeModelAsync(request, ct);

        using var reader = new StreamReader(response.Body);
        var responseBody = await reader.ReadToEndAsync(ct);

        var result = JsonDocument.Parse(responseBody);
        return result.RootElement
            .GetProperty("content")[0]
            .GetProperty("text")
            .GetString() ?? string.Empty;
    }

    public async Task<string> SanitizeHistoryAsync(string rawHistory, CancellationToken ct)
    {
        var messages = new List<ChatMessage>
        {
            new("user", rawHistory)
        };

        return await GetResponseAsync(BotPrompts.SanitizationPrompt, messages, ct);
    }

    private static List<BedrockMessage> FormatChatMessages(List<ChatMessage> history)
    {
        return [.. history.Select(m => new BedrockMessage(
            Role: m.Role.ToLowerInvariant(),
            Content: [new BedrockContentBlock("text", m.Content)]
        ))];
    }
}

public record ChatMessage(string Role, string Content);
public record BedrockMessage(string Role, BedrockContentBlock[] Content);
public record BedrockContentBlock(string Type, string Text);