using AutomaticEnvelopes.Api.Core.Events;
using AutomaticEnvelopes.Api.Features.Chat.Models;
using AutomaticEnvelopes.Api.Features.Knowledge.Services;
using AutomaticEnvelopes.Api.Features.Tenancy;
using Marten;
using Microsoft.Extensions.AI;
using System.Text;
using Wolverine;

namespace AutomaticEnvelopes.Api.Features.Chat.Handlers;

public static class ChatSessionHandler 
{
    public static async Task Handle(
        AnalyzeChatSession command,
        IDocumentStore store,
        IKnowledgeBaseService knowledgeBase,
        IChatService chatService,
        IMessageBus bus,
        ILogger logger,
        CancellationToken ct)
    {
        using var session = store.LightweightSession(command.TenantId);

        var tenant = await session.LoadAsync<TenantProfile>(command.TenantId, ct);
        if (tenant == null)
        {
            logger.LogWarning("TenantProfile with Id '{TenantId}' does not exist. Aborting message processing.", command.TenantId);
            return;
        }

        var userText = command.CombinedText.Trim().ToUpperInvariant();
        if (BotPrompts.DeleteCommands.Contains(userText))
        {
            logger.LogInformation("User requested data deletion. Triggering DeleteChatHistoryCommand for {PhoneNumber}.", command.PhoneNumber);
            await SendDeleteCommandAsync(command, bus, ct);
            return;
        }

        await ProcessResponseAsync(command, tenant, session, knowledgeBase, chatService, bus, logger, ct);
    }

    private static async Task ProcessResponseAsync(
            AnalyzeChatSession command,
            TenantProfile tenant,
            IDocumentSession session,
            IKnowledgeBaseService knowledgeBase,
            IChatService chatService,
            IMessageBus bus,
            ILogger logger,
            CancellationToken ct)
    {
        var chatHistory = await ExtractChatHistory(command.PhoneNumber, session, ct);

        logger.LogInformation("Retrieving relevant context from Vector Database. TenantId: {TenantId}", command.TenantId);
        var context = await GetRelevantContextAsync(knowledgeBase, command.TenantId, command.CombinedText, ct);

        var isFirstMessage = chatHistory.Count <= 1;
        var systemMessage = BuildSystemPrompt(tenant, isFirstMessage, context);

        logger.LogInformation("Triggering chat response generation process. TenantId: {TenantId}", command.TenantId);

        try
        {
            var replyText = await chatService.GetResponseAsync(systemMessage, chatHistory, command.TenantId, ct);

            if (string.IsNullOrWhiteSpace(replyText))
            {
                logger.LogWarning("Bedrock returned an empty or null response. Applying fallback message for TenantId: {TenantId}.", command.TenantId);
                replyText = "I'm sorry, I couldn't process that request.";
            }

            await SaveAndPublishReplyAsync(command, replyText, session, bus, logger, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Fatal error invoking AI generation for TenantId: {TenantId}.", command.TenantId);
            throw;
        }
    }

    private static async Task SendDeleteCommandAsync(AnalyzeChatSession command, IMessageBus bus, CancellationToken ct)
    {
        var syntheticMessageId = $"wamid.grouped.{Guid.NewGuid():N}";

        var ackMessage = new ReplyGenerated(
            syntheticMessageId,
            command.BotPhoneNumberId,
            command.PhoneNumber,
            BotPrompts.DeleteDataAutomaticReply,
            command.TenantId);

        await bus.InvokeAsync(ackMessage, ct);

        var deleteCommand = new DeleteChatHistoryCommand(
            command.PhoneNumber,
            command.TenantId,
            syntheticMessageId,
            command.BotPhoneNumberId);

        await bus.InvokeAsync(deleteCommand, ct);
    }

    private static async Task<List<ChatMessage>> ExtractChatHistory(string phoneNumber, IDocumentSession session, CancellationToken ct)
    {
        var streamEvents = await session.Events.FetchStreamAsync(phoneNumber, token: ct);

        return [.. streamEvents.Select(evt => evt.Data switch
        {
            MessageReceived userMsg => new ChatMessage(ChatRole.User, userMsg.Text),
            ReplyGenerated botReply => new ChatMessage(ChatRole.Assistant, botReply.Text),
            _ => null
        }).OfType<ChatMessage>()];
    }

    private static async Task<string> GetRelevantContextAsync(IKnowledgeBaseService knowledgeBase, string tenantId, string userText, CancellationToken ct)
    {
        var relevantChunks = await knowledgeBase.SearchAsync(tenantId, userText, limit: 10, ct);

        var contextBuilder = new StringBuilder();
        foreach (var chunk in relevantChunks)
        {
            contextBuilder.AppendLine(chunk.Content);
        }

        return contextBuilder.ToString();
    }

    private static string BuildSystemPrompt(TenantProfile tenant, bool isFirstMessage, string context)
    {
        var privacyWarningRule = string.Empty;

        if (isFirstMessage && !string.IsNullOrWhiteSpace(tenant.PrivacyPolicyUrl))
        {
            privacyWarningRule = string.Format(BotPrompts.PrivacyPolicyRule, tenant.PrivacyPolicyUrl);
        }

        var personaPrompt = !string.IsNullOrWhiteSpace(tenant.SystemPrompt)
            ? tenant.SystemPrompt
            : "You are the official Information Assistant for the organization. Your primary mission is to answer questions using EXCLUSIVELY the information provided inside the <context> tags.";

        var currentDate = DateTimeOffset.UtcNow.ToString("dd MMMM yyyy");
        return string.Format(BotPrompts.SystemPromptTemplate, personaPrompt, privacyWarningRule, currentDate, context);
    }

    private static async Task SaveAndPublishReplyAsync(AnalyzeChatSession command, string replyText, IDocumentSession session, IMessageBus bus, ILogger logger, CancellationToken ct)
    {
        var syntheticMessageId = $"wamid.grouped.{Guid.NewGuid():N}";

        var replyEvent = new ReplyGenerated(
            syntheticMessageId,
            command.BotPhoneNumberId,
            command.PhoneNumber,
            replyText,
            command.TenantId);

        session.Events.Append(command.PhoneNumber, replyEvent);
        await session.SaveChangesAsync(ct);

        logger.LogInformation("Bot reply saved to event stream. Dispatching ReplyGenerated event to Wolverine.");
        await bus.InvokeAsync(replyEvent, ct);
    }
}