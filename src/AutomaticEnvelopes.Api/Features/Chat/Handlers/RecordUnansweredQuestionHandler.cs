using AutomaticEnvelopes.Api.Features.Chat;
using AutomaticEnvelopes.Api.Features.Chat.Models;
using Marten;

namespace AutomaticEnvelopes.Api.Features.Chat.Handlers;

public static class RecordUnansweredQuestionHandler
{
    public static Task Handle(RecordUnansweredQuestion command, IUnansweredInbox inbox, CancellationToken ct) =>
        inbox.RecordAsync(command.TenantId, command.ConversationId, command.Question, command.AskedAt, ct);
}
