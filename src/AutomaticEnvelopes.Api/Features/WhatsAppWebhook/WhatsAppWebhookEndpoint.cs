using AutomaticEnvelopes.Api.Common.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Wolverine;
using Wolverine.Http;

namespace AutomaticEnvelopes.Api.Features.WhatsAppWebhook;

public class WhatsAppWebhookEndpoint
{
[WolverineGet("/api/whatsapp/webhook")]
    public IResult VerifyChallenge(
        [FromQuery(Name = "hub.mode")] string? mode,
        [FromQuery(Name = "hub.verify_token")] string? token,
        [FromQuery(Name = "hub.challenge")] string? challenge,
        IOptions<WhatsAppOptions> options,
        ILogger<WhatsAppWebhookEndpoint> logger)
    {
        var verifyToken = options.Value.VerifyToken;

        if (mode == "subscribe" && token == verifyToken && !string.IsNullOrEmpty(challenge))
        {
            return Results.Content(challenge, "text/plain");
        }

        logger.LogWarning("Webhook verification failed. Expected Token: '{Expected}', Received Token: '{Received}'", verifyToken, token);

        return Results.Forbid();
    }

    [WolverinePost("/api/whatsapp/webhook")]
    public async Task<IResult> ReceiveMessage(
        HttpRequest request,
        IWhatsAppPayloadProcessor processor,
        IMessageBus bus,
        ILogger<WhatsAppWebhookEndpoint> logger)
    {
        if (!await processor.IsSignatureValidAsync(request))
        {
            logger.LogWarning("Invalid WhatsApp signature.");
            return Results.Unauthorized();
        }

        var message = await processor.ExtractMessageAsync(request);

        if (message != null)
        {
            await bus.PublishAsync(message);
        }
        else
        {
            logger.LogDebug("Received Payload does not contain a processable message.");
        }

        return Results.Ok();
    }
}