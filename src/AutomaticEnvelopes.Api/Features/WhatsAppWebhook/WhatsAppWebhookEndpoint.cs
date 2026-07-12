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

        bool isModeSubscribe = mode == "subscribe";
        bool isTokenMatch = token == verifyToken;
        bool hasChallenge = !string.IsNullOrEmpty(challenge);

        logger.LogInformation(
            "Received WhatsApp webhook verification request. Mode: {HubMode}, HasChallenge: {HasChallenge}, IsTokenMatch: {IsTokenMatch}",
            mode,
            hasChallenge,
            isTokenMatch);

        if (isModeSubscribe && isTokenMatch && hasChallenge)
        {
            logger.LogInformation("WhatsApp webhook verification successful. Returning HTTP 200 with challenge.");
            return Results.Content(challenge!, "text/plain");
        }

        logger.LogWarning(
            "WhatsApp webhook verification failed. IsModeSubscribe: {IsModeSubscribe}, IsTokenMatch: {IsTokenMatch}, HasChallenge: {HasChallenge}. Returning HTTP 403.",
            isModeSubscribe,
            isTokenMatch,
            hasChallenge);

        return Results.StatusCode(403);
    }

    [WolverinePost("/api/whatsapp/webhook")]
    public async Task<IResult> ReceiveMessage(
        HttpRequest request,
        IWhatsAppPayloadProcessor processor,
        IMessageBus bus,
        ILogger<WhatsAppWebhookEndpoint> logger)
    {
        logger.LogInformation("Received incoming WhatsApp webhook POST request.");

        if (!await processor.IsSignatureValidAsync(request))
        {
            logger.LogWarning("WhatsApp signature validation failed. The computed hash does not match the 'X-Hub-Signature-256' header. Rejecting with HTTP 401.");
            return Results.Unauthorized();
        }

        var message = await processor.ExtractMessageAsync(request);

        if (message != null)
        {
            logger.LogInformation("Successfully extracted processable WhatsApp message. Publishing to message bus.");
            await bus.PublishAsync(message);
        }
        else
        { 
            logger.LogInformation("Received valid WhatsApp webhook payload, but it does not contain a processable user message (e.g., status update or unsupported type). Ignored.");
        }

        return Results.Ok();
    }
}