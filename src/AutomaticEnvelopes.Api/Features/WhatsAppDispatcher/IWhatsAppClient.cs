using Refit;

namespace AutomaticEnvelopes.Api.Features.WhatsAppDispatcher;

public interface IWhatsAppClient
{
    [Post("/{phoneNumberId}/messages")]
    Task<WhatsAppResponse> SendMessageAsync(
        string phoneNumberId,
        [Body] WhatsAppTextRequest request,
        [Header("Authorization")] string authorization,
        CancellationToken ct = default);
}