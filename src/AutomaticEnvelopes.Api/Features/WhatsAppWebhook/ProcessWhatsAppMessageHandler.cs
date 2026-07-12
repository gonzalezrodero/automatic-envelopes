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
        logger.LogInformation("Extracting SQS payload. MessageId: {MessageId}, BotPhoneNumberId: {BotPhoneNumberId}", command.MessageId, command.BotPhoneNumberId);

        using var querySession = store.QuerySession();
        var tenant = await querySession.Query<TenantProfile>()
            .FirstOrDefaultAsync(x => x.BotPhoneNumberId == command.BotPhoneNumberId, ct);

        if (tenant == null)
        {
            logger.LogWarning("Silent abort: No tenant found in PostgreSQL for BotPhoneNumberId: {BotPhoneNumberId}. Verify Meta configuration.", command.BotPhoneNumberId);
            return;
        }

        logger.LogInformation("Tenant identified: {TenantId}. Verifying duplicate messages...", tenant.Id);

        using var session = store.LightweightSession(tenant.Id);
        if (await session.Query<ProcessedMessage>().AnyAsync(x => x.Id == command.MessageId, ct))
        {
            logger.LogWarning("Silent abort: Message {MessageId} already exists in the database. Discarding duplicate from Meta.", command.MessageId);
            return;
        }

        logger.LogInformation("New message detected. Appending MessageReceived event to Marten stream for {PhoneNumber}.", command.PhoneNumber);

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

        logger.LogInformation("Event successfully saved. Delegating to MessageReceivedHandler for MessageId: {MessageId}.", command.MessageId);

        await bus.InvokeAsync(receivedEvent, ct);
    }
}