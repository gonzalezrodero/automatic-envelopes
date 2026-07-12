using AutomaticEnvelopes.Api.Core.Events;
using AutomaticEnvelopes.Api.Features.Tenancy;
using AutomaticEnvelopes.Api.Features.WhatsAppWebhook.Models;
using Marten;
using Wolverine;

namespace AutomaticEnvelopes.Api.Features.WhatsAppWebhook;

public static class ProcessWhatsAppMessageHandler
{
    private const int MaxMessagesPerMinute = 25;
    private const int WindowSizeInMinutes = 1;

    public static async Task Handle(
        ProcessWhatsAppMessage command,
        IDocumentStore store,
        IMessageBus bus,
        ILogger logger,
        CancellationToken ct)
    {
        logger.LogInformation("Extracting SQS payload. MessageId: {MessageId}, BotPhoneNumberId: {BotPhoneNumberId}", command.MessageId, command.BotPhoneNumberId);

        // 1. Resolve Tenant
        var tenant = await GetTenantAsync(store, command.BotPhoneNumberId, ct);
        if (tenant == null)
        {
            logger.LogWarning("Silent abort: No tenant found in PostgreSQL for BotPhoneNumberId: {BotPhoneNumberId}. Verify Meta configuration.", command.BotPhoneNumberId);
            return;
        }

        logger.LogInformation("Tenant identified: {TenantId}. Verifying duplicate messages...", tenant.Id);

        using var session = store.LightweightSession(tenant.Id);

        // 2. Duplicate Check
        if (await IsDuplicateMessageAsync(session, command.MessageId, ct))
        {
            logger.LogWarning("Silent abort: Message {MessageId} already exists in the database. Discarding duplicate from Meta.", command.MessageId);
            return;
        }

        // 3. Spam Shield (Rate Limiting)
        if (!await IsRateLimitPassedAsync(session, command.PhoneNumber, logger, ct))
        {
            return;
        }

        // 4. Atomic Commit & Dispatch
        await CommitAndDispatchEventAsync(command, tenant.Id, session, bus, logger, ct);
    }

    private static async Task<TenantProfile?> GetTenantAsync(IDocumentStore store, string botPhoneNumberId, CancellationToken ct)
    {
        using var querySession = store.QuerySession();
        return await querySession.Query<TenantProfile>()
            .FirstOrDefaultAsync(x => x.BotPhoneNumberId == botPhoneNumberId, ct);
    }

    private static Task<bool> IsDuplicateMessageAsync(IDocumentSession session, string messageId, CancellationToken ct)
    {
        return session.Query<ProcessedMessage>().AnyAsync(x => x.Id == messageId, ct);
    }

    private static async Task<bool> IsRateLimitPassedAsync(IDocumentSession session, string phoneNumber, ILogger logger, CancellationToken ct)
    {
        // Wolverine will automatically retry if multiple Lambdas hit this simultaneously and collide
        var tracker = await session.LoadAsync<WhatsAppRateLimitTracker>(phoneNumber, ct)
                      ?? new WhatsAppRateLimitTracker { Id = phoneNumber };

        var now = DateTimeOffset.UtcNow;

        if (now > tracker.WindowResetTime)
        {
            tracker.MessageCount = 0;
            tracker.WindowResetTime = now.AddMinutes(WindowSizeInMinutes);
        }

        tracker.MessageCount++;

        // Stage the tracker to be saved in the upcoming transaction
        session.Store(tracker);

        if (tracker.MessageCount > MaxMessagesPerMinute)
        {
            logger.LogWarning("Spam shield triggered. Phone {PhoneNumber} dropped. Count: {Count}", phoneNumber, tracker.MessageCount);

            // Commit the tracker immediately to keep the spammer locked out until the time window fully resets
            await session.SaveChangesAsync(ct);
            return false;
        }

        return true;
    }

    private static async Task CommitAndDispatchEventAsync(
        ProcessWhatsAppMessage command,
        string tenantId,
        IDocumentSession session,
        IMessageBus bus,
        ILogger logger,
        CancellationToken ct)
    {
        logger.LogInformation("New message detected. Appending MessageReceived event to Marten stream for {PhoneNumber}.", command.PhoneNumber);

        var receivedEvent = new MessageReceived(
            MessageId: command.MessageId,
            PhoneNumber: command.PhoneNumber,
            Text: command.Text,
            TenantId: tenantId,
            BotPhoneNumberId: command.BotPhoneNumberId,
            ReceivedAt: command.Timestamp
        );

        session.Events.Append(command.PhoneNumber, receivedEvent);

        // If a DB collision happens here, Marten throws a ConcurrencyException
        await session.SaveChangesAsync(ct);

        logger.LogInformation("Event successfully saved. Delegating to Wolverine bus for MessageId: {MessageId}.", command.MessageId);

        await bus.InvokeAsync(receivedEvent, ct);
    }
}