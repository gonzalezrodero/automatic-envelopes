using Marten;
using SamaBot.Api.Core.Events;
using SamaBot.Api.Features.Knowledge.Services;
using SamaBot.Api.Features.Tenancy;
using System.Text;
using Wolverine;

namespace SamaBot.Api.Features.Chat;

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
            await SendDeleteCommandAsync(@event, bus, ct);
            return;
        }

        await ProcessResponseAsync(@event, tenant, session, knowledgeBase, chatService, bus, ct);
    }

    private static async Task ProcessResponseAsync(
        MessageReceived @event,
        TenantProfile tenant,
        IDocumentSession session,
        IKnowledgeBaseService knowledgeBase,
        IChatService chatService,
        IMessageBus bus,
        CancellationToken ct)
    {
        var chatHistory = await ExtractChatHistory(@event.PhoneNumber, session, ct);

        var context = await GetRelevantContextAsync(knowledgeBase, @event.TenantId, @event.Text, ct);

        var isFirstMessage = chatHistory.Count <= 1;
        var systemMessage = BuildSystemPrompt(tenant, isFirstMessage, context);

        var replyText = await chatService.GetResponseAsync(systemMessage, chatHistory, ct);

        if (string.IsNullOrWhiteSpace(replyText))
        {
            replyText = "I'm sorry, I couldn't process that request.";
        }

        await SaveAndPublishReplyAsync(@event, replyText, session, bus, ct);
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
            MessageReceived userMsg => new ChatMessage("user", userMsg.Text),
            ReplyGenerated botReply => new ChatMessage("assistant", botReply.Text),
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

        return string.Format(BotPrompts.SystemPromptTemplate, personaPrompt, privacyWarningRule, context);
    }

    private static async Task SaveAndPublishReplyAsync(MessageReceived @event, string replyText, IDocumentSession session, IMessageBus bus, CancellationToken ct)
    {
        var replyEvent = new ReplyGenerated(
            @event.MessageId,
            @event.BotPhoneNumberId,
            @event.PhoneNumber,
            replyText,
            @event.TenantId);

        session.Events.Append(@event.PhoneNumber, replyEvent);
        await session.SaveChangesAsync(ct);
        await bus.InvokeAsync(replyEvent, ct);
    }
}