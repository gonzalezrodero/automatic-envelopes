using AutomaticEnvelopes.Api.Core.Events;
using AutomaticEnvelopes.Api.Features.Tenancy;
using Marten;
using Wolverine;

namespace AutomaticEnvelopes.Api.Features.WhatsAppWebhook;

public static class ProcessWhatsAppMessageHandler
{
    public static async Task Handle(
        ProcessWhatsAppMessage command,
        IDocumentStore store,
        IMessageBus bus,
        ILogger logger,
        CancellationToken ct)
    {
        logger.LogInformation("--- INICIANDO HANDLER DE WHASTAPP ---");
        logger.LogInformation("Payload SQS extraído -> MessageId: {MessageId}, BotId: {BotPhoneNumberId}",
            command.MessageId, command.BotPhoneNumberId);

        using var querySession = store.QuerySession();
        var tenant = await querySession.Query<TenantProfile>()
            .FirstOrDefaultAsync(x => x.BotPhoneNumberId == command.BotPhoneNumberId, ct);

        if (tenant == null)
        {
            logger.LogWarning(">>> ABORTO SILENCIOSO: No hay ningún Tenant en PostgreSQL con el teléfono: '{BotPhoneNumberId}'. Revisa que Meta esté enviando el ID correcto.", command.BotPhoneNumberId);
            return;
        }

        logger.LogInformation("Tenant encontrado: {TenantId}. Verificando duplicados...", tenant.Id);

        using var session = store.LightweightSession(tenant.Id);
        if (await session.Query<ProcessedMessage>().AnyAsync(x => x.Id == command.MessageId, ct))
        {
            logger.LogWarning(">>> ABORTO SILENCIOSO: El mensaje {MessageId} ya existe en la base de datos (Duplicado de Meta).", command.MessageId);
            return;
        }

        logger.LogInformation("Mensaje nuevo. Guardando evento en Marten y publicando...");

        var receivedEvent = new MessageReceived(
            MessageId: command.MessageId,
            PhoneNumber: command.PhoneNumber,
            Text: command.Text,
            TenantId: tenant.Id,
            BotPhoneNumberId: command.BotPhoneNumberId,
            ReceivedAt: command.Timestamp
        );

        session.Events.Append(command.PhoneNumber, receivedEvent);
        await session.SaveChangesAsync(ct);

        logger.LogInformation("Evento guardado OK. Pasando el testigo al MessageReceivedHandler.");

        await bus.InvokeAsync(receivedEvent, ct);
    }
}