using AutomaticEnvelopes.Api.Features.Chat.Models;

namespace AutomaticEnvelopes.Api.Features.Chat.Handlers;

public static class ForgetUnansweredConversationHandler
{
    public static Task Handle(ForgetUnansweredConversation command, IUnansweredInbox inbox, CancellationToken ct) =>
        inbox.ForgetConversationAsync(command.TenantId, command.ConversationId, ct);
}
