using Amazon.BedrockRuntime.Model;

namespace AutomaticEnvelopes.Api.Features.Chat;

public interface IBedrockTool
{
    string Tenant { get; }

    ToolSpecification GetSpecification();

    Task<string> ExecuteAsync(string jsonArguments, CancellationToken ct);
}