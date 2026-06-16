using AutomaticEnvelopes.Api.Common.Configuration;
using AutomaticEnvelopes.Api.Core.Events;
using Microsoft.Extensions.Options;

namespace AutomaticEnvelopes.Api.Features.WhatsAppDispatcher;

public static class ReplyGeneratedHandler
{
    public static async Task Handle(
        ReplyGenerated @event,
        IWhatsAppClient whatsappClient,
        IOptions<WhatsAppOptions> options,
        CancellationToken ct)
    {
        var config = options.Value;
        ArgumentException.ThrowIfNullOrWhiteSpace(config.AccessToken);

        var token = $"Bearer {config.AccessToken}";

        var request = new WhatsAppTextRequest(
            To: @event.PhoneNumber,
            Text: new WhatsAppMessageBody(@event.Text)
        );

        await whatsappClient.SendMessageAsync(@event.BotPhoneNumberId, request, token, ct);
    }
}