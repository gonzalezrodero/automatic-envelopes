namespace AutomaticEnvelopes.Api.Features.Chat.Models;

public class BedrockSettings
{
    public string Region { get; init; } = string.Empty;
    public string ModelId { get; init; } = string.Empty;
    public int MaxTokens { get; init; } = 0;
    public float Temperature { get; init; } = 0.0f;
}