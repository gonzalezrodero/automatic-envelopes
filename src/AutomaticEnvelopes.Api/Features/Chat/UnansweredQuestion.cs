namespace AutomaticEnvelopes.Api.Features.Chat;

public class UnansweredQuestion
{
    public Guid Id { get; set; }

    public string ConversationId { get; set; } = string.Empty;

    public string Question { get; set; } = string.Empty;

    public DateTimeOffset AskedAt { get; set; }

    public string? Answer { get; set; }

    public DateTimeOffset? AnsweredAt { get; set; }
}
