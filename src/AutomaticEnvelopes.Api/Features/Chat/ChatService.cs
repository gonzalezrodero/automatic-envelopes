using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using AutomaticEnvelopes.Api.Features.Chat.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace AutomaticEnvelopes.Api.Features.Chat;

public interface IChatService
{
    Task<string> GetResponseAsync(string systemPrompt, List<ChatMessage> history, string tenantId, CancellationToken ct);
    Task<string> SanitizeHistoryAsync(string rawHistory, CancellationToken ct);
}

public class ChatService(
    IAmazonBedrockRuntime client,
    IOptions<BedrockSettings> settings,
    IEnumerable<IBedrockTool> tools,
    IToolExecutionService toolExecutionService) : IChatService
{
    private readonly BedrockSettings settings = settings.Value;

    public async Task<string> GetResponseAsync(string systemPrompt, List<ChatMessage> history, string tenantId, CancellationToken ct)
    {
        var tenantTools = tools.Where(t => t.Tenant == tenantId).ToList();

        // 1. Build the generic Converse Request
        var request = new ConverseRequest
        {
            ModelId = settings.ModelId,
            System = [new SystemContentBlock { Text = systemPrompt }],
            Messages = FormatChatMessages(history),
            InferenceConfig = new InferenceConfiguration
            {
                MaxTokens = settings.MaxTokens,
                Temperature = settings.Temperature
            }
        };

        // Attach tools if we have any for this tenant
        if (tenantTools.Count != 0)
        {
            request.ToolConfig = new ToolConfiguration
            {
                Tools = [.. tenantTools.Select(t => new Tool { ToolSpec = t.GetSpecification() })]
            };
        }

        // 2. Initial generic call to Bedrock
        var response = await client.ConverseAsync(request, ct);

        // 3. Handle Tool Interception via the Converse API's StopReason
        if (response.StopReason == StopReason.Tool_use)
        {
            var toolResultMessage = await toolExecutionService.ExecuteToolsAsync(response.Output.Message, tenantTools, ct);

            if (toolResultMessage != null)
            {
                // Append the AI's tool request and our C# tool result to the history
                request.Messages.Add(response.Output.Message);
                request.Messages.Add(toolResultMessage);

                // 4. Send it back to the model for the final human-readable answer
                var finalResponse = await client.ConverseAsync(request, ct);
                return finalResponse.Output.Message.Content.First(c => c.Text != null).Text;
            }
        }

        // 5. Normal text response
        return response.Output.Message.Content.First(c => c.Text != null).Text;
    }

    public async Task<string> SanitizeHistoryAsync(string rawHistory, CancellationToken ct)
    {
        // Create a generic Converse request for the sanitization task
        var request = new ConverseRequest
        {
            ModelId = settings.ModelId,
            System = [new SystemContentBlock { Text = BotPrompts.SanitizationPrompt }],
            Messages =
            [
                new Message
                {
                    Role = ConversationRole.User,
                    Content = [new ContentBlock { Text = rawHistory }]
                }
            ],
            InferenceConfig = new InferenceConfiguration
            {
                MaxTokens = settings.MaxTokens,
                Temperature = 0 // Sanitization works best with 0 temperature for consistency
            }
        };

        var response = await client.ConverseAsync(request, ct);
        return response.Output.Message.Content.First(c => c.Text != null).Text;
    }

    private static List<Message> FormatChatMessages(List<ChatMessage> history)
    {
        return [.. history.Select(m => new Message
        {
            Role = m.Role == ChatRole.User ? ConversationRole.User : ConversationRole.Assistant,
            Content = [new ContentBlock { Text = m.Text ?? string.Empty }]
        })];
    }
}