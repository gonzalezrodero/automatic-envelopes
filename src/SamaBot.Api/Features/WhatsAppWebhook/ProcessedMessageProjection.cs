using JasperFx.Events;
using Marten;
using Marten.Events.Projections;
using SamaBot.Api.Core.Events;

namespace SamaBot.Api.Features.WhatsAppWebhook;

public class ProcessedMessageProjection : IProjection
{
    public void Apply(IDocumentOperations operations, IReadOnlyList<IEvent> events)
    {
        foreach (var @event in events)
        {
            if (@event.Data is MessageReceived messageReceived)
            {
                operations.Store(new ProcessedMessage
                {
                    Id = messageReceived.MessageId,
                    ProcessedAt = messageReceived.ReceivedAt,
                    TenantId = messageReceived.TenantId,
                    BotPhoneNumberId = messageReceived.BotPhoneNumberId
                });
            }
        }
    }

    public Task ApplyAsync(IDocumentOperations operations, IReadOnlyList<IEvent> events, CancellationToken cancellation)
    {
        Apply(operations, events);
        return Task.CompletedTask;
    }
}