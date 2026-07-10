using AutomaticEnvelopes.Api.Core.Events;
using AutomaticEnvelopes.Api.Features.Knowledge.Services;
using AutomaticEnvelopes.Api.Features.Tenancy;
using Marten;
using Microsoft.Extensions.AI;
using System.Text;
using Wolverine;

namespace AutomaticEnvelopes.Api.Features.Chat;

public static class MessageReceivedHandler
{
    public static async Task Handle(
        MessageReceived @event,
        IDocumentStore store,
        IKnowledgeBaseService knowledgeBase,
        IChatService chatService,
        IMessageBus bus,
        ILogger logger,
        CancellationToken ct)
    {
        using var session = store.LightweightSession(@event.TenantId);

        var tenant = await session.LoadAsync<TenantProfile>(@event.TenantId, ct);
        if (tenant == null)
        {
            logger.LogWarning("TenantProfile with Id '{TenantId}' does not exist. Aborting message processing.", @event.TenantId);
            return;
        }

        var userText = @event.Text.Trim().ToUpperInvariant();
        if (BotPrompts.DeleteCommands.Contains(userText))
        {
            logger.LogInformation("User requested data deletion. Triggering DeleteChatHistoryCommand for {PhoneNumber}.", @event.PhoneNumber);
            await SendDeleteCommandAsync(@event, bus, ct);
            return;
        }

        await ProcessResponseAsync(@event, tenant, session, knowledgeBase, chatService, bus, logger, ct);
    }

    private static async Task ProcessResponseAsync(
            MessageReceived @event,
            TenantProfile tenant,
            IDocumentSession session,
            IKnowledgeBaseService knowledgeBase,
            IChatService chatService,
            IMessageBus bus,
            ILogger logger,
            CancellationToken ct)
    {
        var chatHistory = await ExtractChatHistory(@event.PhoneNumber, session, ct);

        logger.LogInformation("Retrieving relevant context from Vector Database. TenantId: {TenantId}", @event.TenantId);
        var context = await GetRelevantContextAsync(knowledgeBase, @event.TenantId, @event.Text, ct);

        var isFirstMessage = chatHistory.Count <= 1;
        var systemMessage = BuildSystemPrompt(tenant, isFirstMessage, context);

        logger.LogInformation("Triggering chat response generation process. TenantId: {TenantId}", @event.TenantId);

        try
        {
            var replyText = await chatService.GetResponseAsync(systemMessage, chatHistory, @event.TenantId, ct);

            if (string.IsNullOrWhiteSpace(replyText))
            {
                logger.LogWarning("Bedrock returned an empty or null response. Applying fallback message for TenantId: {TenantId}.", @event.TenantId);
                replyText = "I'm sorry, I couldn't process that request.";
            }

            await SaveAndPublishReplyAsync(@event, replyText, session, bus, logger, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Fatal error invoking AI generation for TenantId: {TenantId}. MessageId: {MessageId}", @event.TenantId, @event.MessageId);
            throw;
        }
    }

    private static async Task SendDeleteCommandAsync(MessageReceived @event, IMessageBus bus, CancellationToken ct)
    {
        var ackMessage = new ReplyGenerated(
            @event.MessageId,
            @event.BotPhoneNumberId,
            @event.PhoneNumber,
            BotPrompts.DeleteDataAutomaticReply,
            @event.TenantId);

        await bus.InvokeAsync(ackMessage, ct);

        var command = new DeleteChatHistoryCommand(
            @event.PhoneNumber,
            @event.TenantId,
            @event.MessageId,
            @event.BotPhoneNumberId);

        await bus.InvokeAsync(command, ct);
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

    private static async Task SaveAndPublishReplyAsync(MessageReceived @event, string replyText, IDocumentSession session, IMessageBus bus, ILogger logger, CancellationToken ct)
    {
        var replyEvent = new ReplyGenerated(
            @event.MessageId,
            @event.BotPhoneNumberId,
            @event.PhoneNumber,
            replyText,
            @event.TenantId);

        session.Events.Append(@event.PhoneNumber, replyEvent);
        await session.SaveChangesAsync(ct);

        logger.LogInformation("Bot reply saved to event stream. Dispatching ReplyGenerated event to Wolverine.");
        await bus.InvokeAsync(replyEvent, ct);
    }
}