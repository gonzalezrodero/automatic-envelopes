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
        logger.LogInformation("=== STARTING WEBHOOK VERIFICATION (GET) ===");

        var verifyToken = options.Value.VerifyToken;

        bool isModeSubscribe = mode == "subscribe";
        bool isTokenMatch = token == verifyToken;
        bool hasChallenge = !string.IsNullOrEmpty(challenge);

        // Apenas regista valores booleanos seguros para depuração
        logger.LogInformation("Evaluating conditions -> Mode is Subscribe? {IsMode}, Token Matches? {IsTokenMatch}, Has Challenge? {HasChallenge}",
            isModeSubscribe, isTokenMatch, hasChallenge);

        if (isModeSubscribe && isTokenMatch && hasChallenge)
        {
            logger.LogInformation("Webhook verification successful. Returning HTTP 200 with challenge.");
            return Results.Content(challenge, "text/plain");
        }

        logger.LogWarning("Webhook verification failed due to mismatched conditions. Returning HTTP 403.");

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