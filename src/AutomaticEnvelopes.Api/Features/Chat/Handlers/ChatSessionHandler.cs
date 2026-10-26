using AutomaticEnvelopes.Api.Common;
using AutomaticEnvelopes.Api.Core.Events;
using AutomaticEnvelopes.Api.Features.Chat;
using AutomaticEnvelopes.Api.Features.Chat.Models;
using AutomaticEnvelopes.Api.Features.Knowledge.Services;
using AutomaticEnvelopes.Api.Features.Portal;
using AutomaticEnvelopes.Api.Features.Tenancy;
using AutomaticEnvelopes.Api.Features.Usage;
using Marten;
using Microsoft.Extensions.AI;
using System.Text;
using Wolverine;

namespace AutomaticEnvelopes.Api.Features.Chat.Handlers;

public sealed class ChatSessionHandler(
    IDocumentStore store,
    IKnowledgeBaseService knowledgeBase,
    IChatService chatService,
    UsageMeter usage,
    IMessageBus bus,
    ILogger<ChatSessionHandler> logger)
{
    public async Task Handle(AnalyzeChatSession command, CancellationToken ct)
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
            logger.LogInformation("User requested data deletion. Triggering DeleteChatHistoryCommand for {PhoneNumber}.", LogMask.Phone(command.PhoneNumber));
            await SendDeleteCommandAsync(command, ct);
            return;
        }

        await ProcessResponseAsync(command, tenant, session, ct);
    }

    private async Task ProcessResponseAsync(
        AnalyzeChatSession command,
        TenantProfile tenant,
        IDocumentSession session,
        CancellationToken ct)
    {
        var chatHistory = await ExtractChatHistory(command.PhoneNumber, session, ct);
        var work = new ReplyWork(
            command,
            tenant,
            session,
            !chatHistory.Any(message => message.Role == ChatRole.Assistant),
            chatHistory.Count > 0);
        var limit = tenant.EffectiveMonthlyResponseLimit;
        var used = await usage.ResponseCountAsync(tenant.Id, ct);
        if (used >= limit)
        {
            await ReplyAtLimitAsync(work, used, limit, ct);
            return;
        }

        // Before a reply is stored, one fixed message is enough: returning would let SQS run
        // the handler again and send a second WhatsApp. After it is stored, the exception
        // continues so a failed dispatch can be retried instead of being replaced.
        var gate = new SaveGate();
        try
        {
            await PublishGeneratedReplyAsync(work, chatHistory, gate, ct);
        }
        catch (Exception ex) when (!gate.Stored && ex is not OperationCanceledException)
        {
            await DeliverFallbackAsync(ex, work, ct);
        }
    }

    private async Task PublishGeneratedReplyAsync(
        ReplyWork work,
        List<ChatMessage> chatHistory,
        SaveGate gate,
        CancellationToken ct)
    {
        var command = work.Command;
        logger.LogInformation("Retrieving relevant context from Vector Database. TenantId: {TenantId}", command.TenantId);
        var context = await GetRelevantContextAsync(knowledgeBase, command.TenantId, command.CombinedText, ct);
        var systemMessage = BuildSystemPrompt(work.Tenant, context);

        logger.LogInformation("Triggering chat response generation process. TenantId: {TenantId}", command.TenantId);
        var reply = await chatService.GetResponseAsync(
            systemMessage,
            chatHistory,
            command.TenantId,
            work.Tenant.EnabledTools,
            ct);
        var (replyText, unknown) = await VisibleReplyAsync(reply.Text, command, ct);

        if (work.FirstContact)
        {
            replyText = await PrependNoticeAsync(work.Tenant, command.CombinedText, replyText, ct);
        }

        var saved = await SaveAndPublishReplyAsync(command, replyText, work.Session, work.HasHistory, gate, ct);
        if (saved)
        {
            await usage.RecordAsync(work.Tenant, reply.InputTokens, reply.OutputTokens, ct);
        }

        if (unknown && saved)
        {
            await TryRecordUnansweredAsync(command, ct);
        }
    }

    private async Task<(string Text, bool Unknown)> VisibleReplyAsync(
        string replyText,
        AnalyzeChatSession command,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(replyText))
        {
            logger.LogWarning("Bedrock returned an empty or null response. Applying fallback message for TenantId: {TenantId}.", command.TenantId);
            var fallback = await TranslateForUserAsync(UserMessages.Unavailable, command.CombinedText, command.TenantId, ct);
            return (fallback, false);
        }

        if (!UnknownReply.TryStrip(replyText, out var visible))
        {
            return (replyText, false);
        }

        if (!string.IsNullOrWhiteSpace(visible))
        {
            return (visible, true);
        }

        var translated = await TranslateForUserAsync(UnknownReply.Fallback, command.CombinedText, command.TenantId, ct);
        return (translated, true);
    }

    private async Task DeliverFallbackAsync(Exception failure, ReplyWork work, CancellationToken ct)
    {
        var gate = new SaveGate();
        try
        {
            var replyText = await TranslateForUserAsync(UserMessages.Unavailable, work.Command.CombinedText, work.Tenant.Id, ct);
            if (work.FirstContact)
            {
                replyText = await PrependNoticeAsync(work.Tenant, work.Command.CombinedText, replyText, ct);
            }

            var saved = await SaveAndPublishReplyAsync(
                work.Command,
                replyText,
                work.Session,
                work.HasHistory,
                gate,
                ct);
            if (saved)
            {
                logger.LogError(failure, "Chat generation failed. Sent a fixed fallback. TenantId: {TenantId}.", work.Tenant.Id);
                return;
            }

            logger.LogError(failure, "Chat generation failed and the fallback was dropped because the chat was deleted. TenantId: {TenantId}.", work.Tenant.Id);
        }
        catch (Exception delivery) when (gate.Stored && delivery is not OperationCanceledException)
        {
            logger.LogError(failure, "Chat generation failed. TenantId: {TenantId}.", work.Tenant.Id);
            logger.LogError(delivery, "The fallback was stored but could not be dispatched. Not retrying, to avoid a duplicate WhatsApp message. TenantId: {TenantId}.", work.Tenant.Id);
        }
        catch (Exception delivery) when (!gate.Stored && delivery is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"Chat generation failed for tenant {work.Tenant.Id} and the fallback reply was not delivered.",
                new AggregateException(failure, delivery));
        }
    }

    private async Task TryRecordUnansweredAsync(AnalyzeChatSession command, CancellationToken ct)
    {
        try
        {
            await bus.InvokeAsync(
                new RecordUnansweredQuestion(
                    command.TenantId,
                    PortalIds.ForStream(command.PhoneNumber),
                    command.CombinedText,
                    DateTimeOffset.UtcNow),
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The reply was sent, but the unanswered question was not recorded. TenantId: {TenantId}.", command.TenantId);
        }
    }

    private async Task ReplyAtLimitAsync(ReplyWork work, int used, int limit, CancellationToken ct)
    {
        logger.LogInformation(
            "Monthly response limit reached. TenantId: {TenantId}. Responses: {Responses}. Limit: {Limit}.",
            work.Tenant.Id, used, limit);

        var replyText = await TranslateForUserAsync(UsageMessages.Spanish, work.Command.CombinedText, work.Tenant.Id, ct);
        if (work.FirstContact)
        {
            replyText = await PrependNoticeAsync(work.Tenant, work.Command.CombinedText, replyText, ct);
        }

        await SaveAndPublishReplyAsync(work.Command, replyText, work.Session, work.HasHistory, new SaveGate(), ct);
        await usage.NotifyBlockedAsync(work.Tenant, ct);
    }

    private async Task<string> TranslateForUserAsync(string spanish, string userText, string tenantId, CancellationToken ct)
    {
        string? translated = null;
        try
        {
            translated = await chatService.TranslateNoticeAsync(spanish, userText, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "User-facing text translation failed for TenantId: {TenantId}. Using the Spanish text.", tenantId);
        }

        if (!IsUsableTranslation(translated))
        {
            return spanish;
        }

        return translated!.Trim();
    }

    private static bool IsUsableTranslation(string? translated)
    {
        if (string.IsNullOrWhiteSpace(translated))
        {
            return false;
        }

        var text = translated.Trim();
        if (text.Length > BotPrompts.MaxTranslatedNoticeLength)
        {
            return false;
        }

        return !text.Contains("<language_sample>", StringComparison.OrdinalIgnoreCase)
            && !text.Contains("</language_sample>", StringComparison.OrdinalIgnoreCase);
    }

    private async Task SendDeleteCommandAsync(AnalyzeChatSession command, CancellationToken ct)
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

    private static string BuildSystemPrompt(TenantProfile tenant, string context)
    {
        var personaPrompt = tenant.SystemPrompt;
        if (string.IsNullOrWhiteSpace(personaPrompt))
            personaPrompt = "You are the official Information Assistant for the organization. Your primary mission is to answer questions using EXCLUSIVELY the retrieved document data inside <untrusted_document_context>.";

        var currentDate = DateTimeOffset.UtcNow.ToString("dd MMMM yyyy");
        return BotPrompts.Compose(personaPrompt, string.Empty, currentDate, context);
    }

    private async Task<string> PrependNoticeAsync(
        TenantProfile tenant,
        string userText,
        string replyText,
        CancellationToken ct)
    {
        var name = string.IsNullOrWhiteSpace(tenant.Name) ? tenant.ShortName : tenant.Name;
        var notice = BotPrompts.FirstContactNotice(name, tenant.PrivacyPolicyUrl);
        var url = BotPrompts.NoticePrivacyUrl(tenant.PrivacyPolicyUrl);

        string? translated = null;
        try
        {
            translated = await chatService.TranslateNoticeAsync(notice, userText, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "First-contact notice translation failed for TenantId: {TenantId}. Using the Spanish notice.", tenant.Id);
        }

        if (!BotPrompts.IsFaithfulTranslation(translated, url))
            translated = notice;

        return translated.Trim() + "\n\n" + replyText;
    }

    private async Task<bool> SaveAndPublishReplyAsync(
        AnalyzeChatSession command,
        string replyText,
        IDocumentSession session,
        bool dropIfStreamDeleted,
        SaveGate gate,
        CancellationToken ct)
    {
        if (dropIfStreamDeleted)
        {
            var current = await session.Events.FetchStreamAsync(command.PhoneNumber, token: ct);
            if (current.Count == 0)
            {
                logger.LogInformation(
                    "Dropping a reply for a chat stream that was deleted. Phone: {Phone}",
                    LogMask.Phone(command.PhoneNumber));
                return false;
            }
        }

        var syntheticMessageId = $"wamid.grouped.{Guid.NewGuid():N}";

        var replyEvent = new ReplyGenerated(
            syntheticMessageId,
            command.BotPhoneNumberId,
            command.PhoneNumber,
            replyText,
            command.TenantId);

        session.Events.Append(command.PhoneNumber, replyEvent);
        await session.SaveChangesAsync(ct);
        gate.Stored = true;

        logger.LogInformation("Bot reply saved to event stream. Dispatching ReplyGenerated event to Wolverine.");
        await bus.InvokeAsync(replyEvent, ct);
        return true;
    }

    private sealed record ReplyWork(
        AnalyzeChatSession Command,
        TenantProfile Tenant,
        IDocumentSession Session,
        bool FirstContact,
        bool HasHistory);

    private sealed class SaveGate
    {
        public bool Stored { get; set; }
    }
}
