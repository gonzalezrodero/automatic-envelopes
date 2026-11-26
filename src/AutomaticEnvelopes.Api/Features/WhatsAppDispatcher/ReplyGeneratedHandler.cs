using AutomaticEnvelopes.Api.Common;
using AutomaticEnvelopes.Api.Core.Events;

namespace AutomaticEnvelopes.Api.Features.WhatsAppDispatcher;

/// <summary>
/// The debounce handler opens a slot, and a failed Graph send marks that same object.
/// A bool on AsyncLocal would not be visible to the caller after the send returns.
/// </summary>
public static class WhatsAppSendFailure
{
    private static readonly AsyncLocal<State?> current = new();

    public static bool Occurred => current.Value?.Failed == true;

    public static void Open() => current.Value = new State();

    public static void Mark()
    {
        if (current.Value is { } state)
            state.Failed = true;
    }

    public static void Close() => current.Value = null;

    private sealed class State
    {
        public bool Failed;
    }
}

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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Do not rethrow. A thrown send failure rolls back the debounce transaction,
            // leaves the window open, and the system message retries this same reply.
            logger.LogError(ex, "Failed to send WhatsApp message to Meta API. PhoneNumber: {PhoneNumber}, TenantId: {TenantId}", LogMask.Phone(@event.PhoneNumber), @event.TenantId);
            WhatsAppSendFailure.Mark();
        }
    }
}