namespace AutomaticEnvelopes.Api.Features.Chat;

public interface IPersonalDataFilter
{
    Task<string> CleanAsync(string text, CancellationToken ct);
}

public sealed class ChatPersonalDataFilter(IChatService chat) : IPersonalDataFilter
{
    public async Task<string> CleanAsync(string text, CancellationToken ct)
    {
        var cleaned = await chat.SanitizeHistoryAsync(text, ct);
        return cleaned.Trim();
    }
}
