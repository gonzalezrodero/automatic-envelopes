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

        // 1. LOG EXHAUSTIVO DE ENTRADA Y CONFIGURACIÓN
        logger.LogInformation("=== INICIANDO VERIFICACIÓN DE WEBHOOK (GET) ===");
        logger.LogInformation("1. Datos recibidos de Meta -> hub.mode: '{Mode}', hub.verify_token: '{Token}', hub.challenge: '{Challenge}'",
            mode ?? "NULL", token ?? "NULL", challenge ?? "NULL");
        logger.LogInformation("2. Configuración interna leída -> WhatsAppOptions.VerifyToken: '{VerifyToken}'",
            verifyToken ?? "NULL");

        // 2. EVALUACIÓN PASO A PASO
        bool isModeSubscribe = mode == "subscribe";
        bool isTokenValid = token == verifyToken;
        bool hasChallenge = !string.IsNullOrEmpty(challenge);

        logger.LogInformation("3. Evaluando condiciones -> Mode is Subscribe? {IsMode}, Token Matches? {IsTokenMatch}, Has Challenge? {HasChallenge}",
            isModeSubscribe, isTokenValid, hasChallenge);

        if (isModeSubscribe && isTokenValid && hasChallenge)
        {
            logger.LogInformation("4. ÉXITO: La verificación ha pasado. Devolviendo challenge a Meta.");
            return Results.Content(challenge!, "text/plain");
        }

        // 3. LOG DE FALLO DETALLADO
        logger.LogWarning("4. FALLO: La verificación fue rechazada. Devolviendo HTTP 403.");
        return Results.StatusCode(403);
    }

    [WolverinePost("/api/whatsapp/webhook")]
    public async Task<IResult> ReceiveMessage(
        HttpRequest request,
        IWhatsAppPayloadProcessor processor,
        IMessageBus bus,
        ILogger<WhatsAppWebhookEndpoint> logger)
    {
        logger.LogInformation("=== MENSAJE RECIBIDO DE META (POST) ===");

        // 1. INSPECCIONAR CABECERAS (Para ver si llega la firma)
        var signatureHeader = request.Headers["X-Hub-Signature-256"].FirstOrDefault();
        logger.LogInformation("Cabecera X-Hub-Signature-256: '{Signature}'", signatureHeader ?? "NO ENVIADA");

        // 2. VALIDAR FIRMA
        logger.LogInformation("Iniciando validación de firma con el AppSecret...");
        bool isValid = await processor.IsSignatureValidAsync(request);

        if (!isValid)
        {
            logger.LogWarning("FALLO: La firma de WhatsApp es inválida o no coincide con el AppSecret. Rechazando petición (401).");
            return Results.Unauthorized();
        }
        logger.LogInformation("ÉXITO: La firma es válida.");

        // 3. EXTRAER MENSAJE
        logger.LogInformation("Extrayendo payload del mensaje...");
        var message = await processor.ExtractMessageAsync(request);

        if (message != null)
        {
            logger.LogInformation("Mensaje extraído correctamente. Tipo: {MessageType}. Publicando en el MessageBus...", message.GetType().Name);
            await bus.PublishAsync(message);
            logger.LogInformation("Mensaje publicado en el bus.");
        }
        else
        {
            logger.LogDebug("AVISO: El payload recibido no contiene un mensaje procesable (puede ser un evento de status, lectura, etc). Ignorando.");
        }

        return Results.Ok();
    }
}