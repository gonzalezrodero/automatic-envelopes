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
        ILogger logger,
        CancellationToken ct)
    {
        var config = options.Value;
        ArgumentException.ThrowIfNullOrWhiteSpace(config.AccessToken);

        var token = $"Bearer {config.AccessToken}";

        var request = new WhatsAppTextRequest(
            To: @event.PhoneNumber,
            Text: new WhatsAppMessageBody(@event.Text)
        );

        logger.LogInformation("Enviando respuesta a WhatsApp para el número {PhoneNumber} (Tenant: {TenantId})",
            @event.PhoneNumber, @event.TenantId);

        try
        {
            await whatsappClient.SendMessageAsync(@event.BotPhoneNumberId, request, token, ct);
            logger.LogInformation("Mensaje enviado con éxito a la API de Meta.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[ERROR WHATSAPP API] Fallo al enviar el mensaje a Meta para el número {PhoneNumber}", @event.PhoneNumber);
            throw; 
        }
    }
}