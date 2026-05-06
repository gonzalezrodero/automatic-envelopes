namespace SamaBot.Api.Core.Events;

public record AnonymizedChat(
    Guid Id,
    DateTimeOffset ArchivedAt,
    string Transcript
);