using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using AutomaticEnvelopes.Api.Features.Tenants.Helpers;

namespace AutomaticEnvelopes.Api.Features.Chat.Tools;

public interface IToolExecutionService
{
    Task<Message?> ExecuteToolsAsync(Message response, IEnumerable<IBedrockTool> availableTools, CancellationToken ct);
}

public class ToolExecutionService(ILogger<ToolExecutionService> logger) : IToolExecutionService
{
    public async Task<Message?> ExecuteToolsAsync(Message response, IEnumerable<IBedrockTool> availableTools, CancellationToken ct)
    {
        // 1. Find the tool request from the AI's generic message content
        var toolUseBlock = response.Content.FirstOrDefault(c => c.ToolUse != null)?.ToolUse;
        if (toolUseBlock == null) return null;

        logger.LogInformation("AI requested tool execution. ToolName: '{ToolName}', ToolUseId: '{ToolUseId}'", toolUseBlock.Name, toolUseBlock.ToolUseId);

        var targetTool = availableTools.FirstOrDefault(t => t.GetSpecification().Name == toolUseBlock.Name);
        if (targetTool == null)
        {
            logger.LogWarning("Requested tool '{ToolName}' was not found in the available tools for this tenant. Aborting execution.", toolUseBlock.Name);
            return null;
        }

        // 2. Execute the C# Native AOT logic
        var inputJsonString = JsonConverter.ToJsonString(toolUseBlock.Input);
        logger.LogDebug("Executing tool '{ToolName}' with arguments: {InputJson}", toolUseBlock.Name, inputJsonString);

        var resultJson = await targetTool.ExecuteAsync(inputJsonString, ct);

        logger.LogInformation("Tool '{ToolName}' executed successfully. Returning results to Bedrock.", toolUseBlock.Name);

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