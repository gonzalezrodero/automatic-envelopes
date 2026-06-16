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
        CancellationToken ct)
    {
        using var querySession = store.QuerySession();
        var tenant = await querySession.Query<TenantProfile>()
            .FirstOrDefaultAsync(x => x.BotPhoneNumberId == command.BotPhoneNumberId, ct);

        if (tenant == null)
        {
            return;
        }

        using var session = store.LightweightSession(tenant.Id);
        if (await session.Query<ProcessedMessage>().AnyAsync(x => x.Id == command.MessageId, ct))
        {
            return;
        }

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

        await bus.InvokeAsync(receivedEvent, ct);
    }
}