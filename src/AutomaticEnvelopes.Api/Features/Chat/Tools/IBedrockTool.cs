using Amazon.BedrockRuntime.Model;

namespace AutomaticEnvelopes.Api.Features.Chat.Tools;

public interface IBedrockTool
{
    string Name { get; }

    ToolSpecification GetSpecification();

    Task<string> ExecuteAsync(string jsonArguments, CancellationToken ct);
}