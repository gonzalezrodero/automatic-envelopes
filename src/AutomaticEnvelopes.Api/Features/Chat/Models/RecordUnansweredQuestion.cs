namespace AutomaticEnvelopes.Api.Features.Chat.Models;

public record RecordUnansweredQuestion(
    string TenantId,
    string ConversationId,
    string Question,
    DateTimeOffset AskedAt);
