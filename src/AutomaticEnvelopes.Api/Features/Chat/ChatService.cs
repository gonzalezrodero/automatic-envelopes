using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using AutomaticEnvelopes.Api.Features.Chat.Models;
using AutomaticEnvelopes.Api.Features.Chat.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace AutomaticEnvelopes.Api.Features.Chat;

public readonly record struct ChatReply(string Text, long InputTokens, long OutputTokens);

public interface IChatService
{
    Task<ChatReply> GetResponseAsync(
        string systemPrompt,
        List<ChatMessage> history,
        string tenantId,
        IReadOnlyCollection<string>? enabledTools,
        CancellationToken ct);
    Task<string> TranslateNoticeAsync(string notice, string languageSample, CancellationToken ct);
    Task<string> SanitizeHistoryAsync(string rawHistory, CancellationToken ct);
}

public class ChatService(
    IAmazonBedrockRuntime client,
    IOptions<BedrockSettings> settings,
    IEnumerable<IBedrockTool> tools,
    IToolExecutionService toolExecutionService,
    ILogger<ChatService> logger) : IChatService
{
    private readonly BedrockSettings settings = settings.Value;

    public async Task<ChatReply> GetResponseAsync(
        string systemPrompt,
        List<ChatMessage> history,
        string tenantId,
        IReadOnlyCollection<string>? enabledTools,
        CancellationToken ct)
    {
        // Missing or empty config attaches nothing. Tools are opt-in per tenant.
        var tenantTools = TenantToolGate.Select(tools, enabledTools, TenantToolGate.KnownNames);

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
            logger.LogInformation("Attaching {ToolCount} tools to Bedrock request for TenantId: {TenantId}.", tenantTools.Count, tenantId);
            request.ToolConfig = new ToolConfiguration
            {
                Tools = [.. tenantTools.Select(t => new Tool { ToolSpec = t.GetSpecification() })]
            };
        }

        // 2. Initial generic call to Bedrock
        logger.LogInformation("Invoking AWS Bedrock Converse API. ModelId: {ModelId}, TenantId: {TenantId}", settings.ModelId, tenantId);
        var response = await client.ConverseAsync(request, ct);
        var usage = ReadUsage(response);

        // 3. Handle Tool Interception via the Converse API's StopReason
        if (response.StopReason == StopReason.Tool_use)
        {
            logger.LogInformation("Bedrock requested tool execution. Intercepting response.");
            var toolResultMessage = await toolExecutionService.ExecuteToolsAsync(response.Output.Message, tenantTools, ct);

            if (toolResultMessage != null)
            {
                // Append the AI's tool request and our C# tool result to the history
                request.Messages.Add(response.Output.Message);
                request.Messages.Add(toolResultMessage);

                // 4. Send it back to the model for the final human-readable answer
                logger.LogInformation("Tool results appended to history. Re-invoking Bedrock for final response.");
                var finalResponse = await client.ConverseAsync(request, ct);
                var finalUsage = ReadUsage(finalResponse);

                logger.LogInformation("Final Bedrock response received successfully.");
                return new ChatReply(
                    ReadText(finalResponse),
                    usage.Input + finalUsage.Input,
                    usage.Output + finalUsage.Output);
            }
        }

        // 5. Normal text response
        logger.LogInformation("Bedrock text response generated successfully without tool usage.");
        return new ChatReply(ReadText(response), usage.Input, usage.Output);
    }

    public async Task<string> TranslateNoticeAsync(string notice, string languageSample, CancellationToken ct)
    {
        logger.LogInformation("Invoking Bedrock to translate the first-contact notice.");

        var request = new ConverseRequest
        {
            ModelId = settings.ModelId,
            System = [new SystemContentBlock { Text = $"{BotPrompts.NoticeTranslationPrompt}\n\n{notice}" }],
            Messages =
            [
                new Message
                {
                    Role = ConversationRole.User,
                    Content = [new ContentBlock { Text = BotPrompts.DelimitLanguageSample(languageSample) }]
                }
            ],
            InferenceConfig = new InferenceConfiguration
            {
                MaxTokens = 400,
                Temperature = 0
            }
        };

        var response = await client.ConverseAsync(request, ct);
        return response.Output.Message.Content.First(c => c.Text != null).Text;
    }

    public async Task<string> SanitizeHistoryAsync(string rawHistory, CancellationToken ct)
    {
        logger.LogInformation("Invoking Bedrock to sanitize chat history.");

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

        logger.LogInformation("History sanitization completed successfully.");
        return response.Output.Message.Content.First(c => c.Text != null).Text;
    }

    private static string ReadText(ConverseResponse response) =>
        response.Output.Message.Content.First(c => c.Text != null).Text;

    private static (long Input, long Output) ReadUsage(ConverseResponse response)
    {
        var usage = response.Usage;
        return (usage?.InputTokens ?? 0, usage?.OutputTokens ?? 0);
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