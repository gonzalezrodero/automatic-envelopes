using Amazon.BedrockRuntime.Model;
using AutomaticEnvelopes.Api.Common;
using AutomaticEnvelopes.Api.Core.Events;
using AutomaticEnvelopes.Api.Features.Chat;
using AutomaticEnvelopes.Api.Features.Chat.Models;
using AutomaticEnvelopes.Api.Features.Knowledge.Services;
using AutomaticEnvelopes.Api.Features.Portal;
using AutomaticEnvelopes.Api.Features.Tenancy;
using AutomaticEnvelopes.Api.Features.Usage;
using JasperFx;
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
        UsageMeter usage,
        IMessageBus bus,
        ILogger logger,
        CancellationToken ct)
    {
        var deps = new Deps(knowledgeBase, chatService, usage, bus, logger);
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
            await SendDeleteCommandAsync(command, deps, ct);
            return;
        }

        await ProcessResponseAsync(command, tenant, session, deps, ct);
    }

    private static async Task ProcessResponseAsync(
        AnalyzeChatSession command,
        TenantProfile tenant,
        IDocumentSession session,
        Deps deps,
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
        var used = await deps.Usage.ResponseCountAsync(tenant.Id, ct);
        if (used >= limit)
        {
            await ReplyAtLimitAsync(work, used, limit, deps, ct);
            return;
        }

        // Ordinary failures get one fixed Spanish reply. Throttling, concurrency, and
        // cancellation keep their exception. A stored reply whose WhatsApp send fails
        // also continues, so SQS does not treat the message as done.
        var gate = new SaveGate();
        try
        {
            await PublishGeneratedReplyAsync(work, chatHistory, gate, deps, ct);
        }
        catch (Exception ex) when (!gate.Stored
            && ex is not OperationCanceledException
            && ex is not ThrottlingException
            && ex is not ConcurrencyException)
        {
            await DeliverFallbackAsync(ex, work, deps, ct);
        }
    }

    private static async Task PublishGeneratedReplyAsync(
        ReplyWork work,
        List<ChatMessage> chatHistory,
        SaveGate gate,
        Deps deps,
        CancellationToken ct)
    {
        var command = work.Command;
        deps.Logger.LogInformation("Retrieving relevant context from Vector Database. TenantId: {TenantId}", command.TenantId);
        var context = await GetRelevantContextAsync(deps.Knowledge, command.TenantId, command.CombinedText, ct);
        var systemMessage = BuildSystemPrompt(work.Tenant, context);

        deps.Logger.LogInformation("Triggering chat response generation process. TenantId: {TenantId}", command.TenantId);
        var reply = await deps.Chat.GetResponseAsync(
            systemMessage,
            chatHistory,
            command.TenantId,
            work.Tenant.EnabledTools,
            ct);
        var (replyText, unknown) = VisibleReply(reply.Text, command, deps.Logger);

        if (work.FirstContact)
        {
            replyText = await PrependNoticeAsync(work.Tenant, command.CombinedText, replyText, deps, ct);
        }

        var saved = await SaveAndPublishReplyAsync(work, replyText, gate, deps, ct);
        if (saved)
        {
            await deps.Usage.RecordAsync(work.Tenant, reply.InputTokens, reply.OutputTokens, ct);
        }

        if (unknown && saved)
        {
            await TryRecordUnansweredAsync(command, deps, ct);
        }
    }

    private static (string Text, bool Unknown) VisibleReply(string replyText, AnalyzeChatSession command, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(replyText))
        {
            logger.LogWarning("Bedrock returned an empty or null response. Applying fallback message for TenantId: {TenantId}.", command.TenantId);
            return (UserMessages.Unavailable, false);
        }

        if (!UnknownReply.TryStrip(replyText, out var visible))
        {
            return (replyText, false);
        }

        if (!string.IsNullOrWhiteSpace(visible))
        {
            return (visible, true);
        }

        return (UnknownReply.Fallback, true);
    }

    private static async Task DeliverFallbackAsync(Exception failure, ReplyWork work, Deps deps, CancellationToken ct)
    {
        var gate = new SaveGate();
        try
        {
            var replyText = UserMessages.Unavailable;
            if (work.FirstContact)
            {
                replyText = SpanishNotice(work.Tenant) + "\n\n" + replyText;
            }

            deps.Logger.LogError(failure, "Chat generation failed. Sending a fixed fallback. TenantId: {TenantId}.", work.Tenant.Id);
            var saved = await SaveAndPublishReplyAsync(work, replyText, gate, deps, ct);
            if (!saved)
            {
                deps.Logger.LogInformation(
                    "The fixed fallback was dropped because the chat was deleted. TenantId: {TenantId}.",
                    work.Tenant.Id);
            }
        }
        catch (Exception delivery) when (!gate.Stored && delivery is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"Chat generation failed for tenant {work.Tenant.Id} and the fallback reply was not delivered.",
                new AggregateException(failure, delivery));
        }
    }

    private static async Task TryRecordUnansweredAsync(AnalyzeChatSession command, Deps deps, CancellationToken ct)
    {
        try
        {
            await deps.Bus.InvokeAsync(
                new RecordUnansweredQuestion(
                    command.TenantId,
                    PortalIds.ForStream(command.PhoneNumber),
                    command.CombinedText,
                    DateTimeOffset.UtcNow),
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            deps.Logger.LogError(ex, "The reply was sent, but the unanswered question was not recorded. TenantId: {TenantId}.", command.TenantId);
        }
    }

    private static async Task ReplyAtLimitAsync(ReplyWork work, int used, int limit, Deps deps, CancellationToken ct)
    {
        deps.Logger.LogInformation(
            "Monthly response limit reached. TenantId: {TenantId}. Responses: {Responses}. Limit: {Limit}.",
            work.Tenant.Id, used, limit);

        var replyText = UsageMessages.Spanish;
        if (work.FirstContact)
        {
            replyText = SpanishNotice(work.Tenant) + "\n\n" + replyText;
        }

        await SaveAndPublishReplyAsync(work, replyText, new SaveGate(), deps, ct);
        await deps.Usage.NotifyBlockedAsync(work.Tenant, ct);
    }

    private static string SpanishNotice(TenantProfile tenant)
    {
        var name = string.IsNullOrWhiteSpace(tenant.Name) ? tenant.ShortName : tenant.Name;
        return BotPrompts.FirstContactNotice(name, tenant.PrivacyPolicyUrl).Trim();
    }

    private static async Task SendDeleteCommandAsync(AnalyzeChatSession command, Deps deps, CancellationToken ct)
    {
        var syntheticMessageId = $"wamid.grouped.{Guid.NewGuid():N}";

        var ackMessage = new ReplyGenerated(
            syntheticMessageId,
            command.BotPhoneNumberId,
            command.PhoneNumber,
            BotPrompts.DeleteDataAutomaticReply,
            command.TenantId);

        await deps.Bus.InvokeAsync(ackMessage, ct);

        var deleteCommand = new DeleteChatHistoryCommand(
            command.PhoneNumber,
            command.TenantId,
            syntheticMessageId,
            command.BotPhoneNumberId);

        await deps.Bus.InvokeAsync(deleteCommand, ct);
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

    private static async Task<string> PrependNoticeAsync(
        TenantProfile tenant,
        string userText,
        string replyText,
        Deps deps,
        CancellationToken ct)
    {
        var name = string.IsNullOrWhiteSpace(tenant.Name) ? tenant.ShortName : tenant.Name;
        var notice = BotPrompts.FirstContactNotice(name, tenant.PrivacyPolicyUrl);
        var url = BotPrompts.NoticePrivacyUrl(tenant.PrivacyPolicyUrl);

        string? translated = null;
        try
        {
            translated = await deps.Chat.TranslateNoticeAsync(notice, userText, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            deps.Logger.LogWarning(ex, "First-contact notice translation failed for TenantId: {TenantId}. Using the Spanish notice.", tenant.Id);
        }

        if (!BotPrompts.IsFaithfulTranslation(translated, url))
            translated = notice;

        return translated.Trim() + "\n\n" + replyText;
    }

    private static async Task<bool> SaveAndPublishReplyAsync(
        ReplyWork work,
        string replyText,
        SaveGate gate,
        Deps deps,
        CancellationToken ct)
    {
        var command = work.Command;
        if (work.HasHistory)
        {
            var current = await work.Session.Events.FetchStreamAsync(command.PhoneNumber, token: ct);
            if (current.Count == 0)
            {
                deps.Logger.LogInformation(
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

        work.Session.Events.Append(command.PhoneNumber, replyEvent);
        await work.Session.SaveChangesAsync(ct);
        gate.Stored = true;

        deps.Logger.LogInformation("Bot reply saved to event stream. Dispatching ReplyGenerated event to Wolverine.");
        await deps.Bus.InvokeAsync(replyEvent, ct);
        return true;
    }

    private sealed record Deps(
        IKnowledgeBaseService Knowledge,
        IChatService Chat,
        UsageMeter Usage,
        IMessageBus Bus,
        ILogger Logger);

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
