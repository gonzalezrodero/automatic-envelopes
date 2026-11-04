using AutomaticEnvelopes.Api.Common;
using AutomaticEnvelopes.Api.Core.Events;

namespace AutomaticEnvelopes.Api.Features.WhatsAppDispatcher;

public static class ReplyGeneratedHandler
{
    public static async Task Handle(
        ReplyGenerated @event,
        IWhatsAppClient whatsappClient,
        IWhatsAppAccessTokenSource accessTokens,
        ILogger logger,
        CancellationToken ct)
    {
        // BotPhoneNumberId on the event is the tenant's Graph phone number id.
        var accessToken = await accessTokens.GetAccessTokenAsync(@event.TenantId, ct);
        var token = $"Bearer {accessToken}";

        var request = new WhatsAppTextRequest(
            To: @event.PhoneNumber,
            Text: new WhatsAppMessageBody(@event.Text)
        );

        logger.LogInformation("Dispatching reply to WhatsApp Graph API. PhoneNumber: {PhoneNumber}, TenantId: {TenantId}", LogMask.Phone(@event.PhoneNumber), @event.TenantId);

        try
        {
            await whatsappClient.SendMessageAsync(@event.BotPhoneNumberId, request, token, ct);
            logger.LogInformation("Message successfully dispatched to Meta API for PhoneNumber: {PhoneNumber}.", LogMask.Phone(@event.PhoneNumber));
        }
        catch (Exception ex)
        {
            // Logs the raw HTTP exception details for troubleshooting Meta token/permission errors
            logger.LogError(ex, "Failed to send WhatsApp message to Meta API. PhoneNumber: {PhoneNumber}, TenantId: {TenantId}", LogMask.Phone(@event.PhoneNumber), @event.TenantId);
            throw;
        }
    }
}