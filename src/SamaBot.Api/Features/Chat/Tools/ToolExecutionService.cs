using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using SamaBot.Api.Features.Tenants.Helpers;

namespace SamaBot.Api.Features.Chat.Tools;

public interface IToolExecutionService
{
    Task<Message?> ExecuteToolsAsync(Message response, IEnumerable<IBedrockTool> availableTools, CancellationToken ct);
}

public class ToolExecutionService : IToolExecutionService
{
    public async Task<Message?> ExecuteToolsAsync(Message response, IEnumerable<IBedrockTool> availableTools, CancellationToken ct)
    {
        // 1. Find the tool request from the AI's generic message content
        var toolUseBlock = response.Content.FirstOrDefault(c => c.ToolUse != null)?.ToolUse;
        if (toolUseBlock == null) return null;

        var targetTool = availableTools.FirstOrDefault(t => t.GetSpecification().Name == toolUseBlock.Name);
        if (targetTool == null) return null;

        // 2. Execute the C# Native AOT logic
        var inputJsonString = JsonConverter.ToJsonString(toolUseBlock.Input);
        var resultJson = await targetTool.ExecuteAsync(inputJsonString, ct);

        // 3. Create the generic Tool Result Block to send back to the AI
        var toolResultContent = new ToolResultContentBlock { Text = resultJson };

        var userMessage = new Message
        {
            Role = ConversationRole.User,
            Content =
            [
                new ContentBlock
                {
                    ToolResult = new ToolResultBlock
                    {
                        ToolUseId = toolUseBlock.ToolUseId,
                        Content = [toolResultContent]
                    }
                }
            ]
        };

        return userMessage;
    }
}