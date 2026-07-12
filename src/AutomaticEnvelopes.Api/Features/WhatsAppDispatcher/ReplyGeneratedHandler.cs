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

        logger.LogInformation("Dispatching reply to WhatsApp Graph API. PhoneNumber: {PhoneNumber}, TenantId: {TenantId}", @event.PhoneNumber, @event.TenantId);

        try
        {
            await whatsappClient.SendMessageAsync(@event.BotPhoneNumberId, request, token, ct);
            logger.LogInformation("Message successfully dispatched to Meta API for PhoneNumber: {PhoneNumber}.", @event.PhoneNumber);
        }
        catch (Exception ex)
        {
            // Logs the raw HTTP exception details for troubleshooting Meta token/permission errors
            logger.LogError(ex, "Failed to send WhatsApp message to Meta API. PhoneNumber: {PhoneNumber}, TenantId: {TenantId}", @event.PhoneNumber, @event.TenantId);
            throw;
        }
    }
}