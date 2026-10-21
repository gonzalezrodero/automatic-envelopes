using AutomaticEnvelopes.Api.Core.Events;
using AutomaticEnvelopes.Api.Features.Tenancy;
using AutomaticEnvelopes.Api.Features.Usage;
using AutomaticEnvelopes.Api.Features.WhatsAppWebhook;
using AutomaticEnvelopes.Api.Features.WhatsAppWebhook.Models;
using AutomaticEnvelopes.Tests.Extensions;
using AwesomeAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AutomaticEnvelopes.Tests.Features.WhatsAppWebhook;

[Collection("Integration")]
public class ProcessWhatsAppMessageHandlerTests(IntegrationAppFixture fixture)
{
    [Fact]
    public async Task GivenValidMessageCommand_WhenHandlerExecutes_ThenItAppendsToMartenEventStore()
    {
        // Arrange
        var tenantSlug = "test-tenant-handler-1";
        var botPhoneId = "12345";

        await fixture.SeedTenantAsync(tenantSlug, botPhoneId);

        var command = new ProcessWhatsAppMessage(
            MessageId: "wamid.HANDLER",
            BotPhoneNumberId: botPhoneId, // The command comes with the Meta ID
            PhoneNumber: "34999111222",
            Text: "Pure Handler Text",
            Timestamp: DateTimeOffset.UtcNow
        );

        // Act: Directly invoke the message bypassing the HTTP layer
        await fixture.Host.InvokeMessageAndWaitAsync(command);

        // Assert: The message was correctly sourced in the database
        using var session = fixture.Host.Services.GetRequiredService<IDocumentStore>().LightweightSession(tenantSlug);

        var streamEvents = await session.Events.FetchStreamAsync("34999111222", token: TestContext.Current.CancellationToken);

        streamEvents.Should().NotBeEmpty();

        var messageReceived = streamEvents.FirstOrDefault(e => e.Data is MessageReceived)?.Data as MessageReceived;
        messageReceived.Should().NotBeNull();
        messageReceived!.MessageId.Should().Be("wamid.HANDLER");
        messageReceived.Text.Should().Be("Pure Handler Text");
    }

    [Fact]
    public async Task GivenDuplicateMessageId_WhenHandlerExecutes_ThenItIgnoresSilentlyForIdempotency()
    {
        // Arrange
        var tenantSlug = "test-tenant-handler-2";
        var botPhoneId = "123";

        await fixture.SeedTenantAsync(tenantSlug, botPhoneId);

        var command = new ProcessWhatsAppMessage("wamid.DUP", botPhoneId, "34999111222", "Text payload", DateTimeOffset.UtcNow);

        // Act: Send the same message twice to simulate Meta webhook retries
        await fixture.Host.InvokeMessageAndWaitAsync(command);
        await fixture.Host.InvokeMessageAndWaitAsync(command);

        // Assert: It should only exist once in the stream
        using var session = fixture.Host.Services.GetRequiredService<IDocumentStore>().LightweightSession(tenantSlug);

        var streamEvents = await session.Events.FetchStreamAsync("34999111222", token: TestContext.Current.CancellationToken);

        var receivedCount = streamEvents.Count(e => e.Data is MessageReceived mr && mr.MessageId == "wamid.DUP");
        receivedCount.Should().Be(1);
    }

    [Fact]
    public async Task GivenDuplicateMessageId_WhenHandled_ThenIdempotencyIgnoresSecondAttempt()
    {
        // Arrange
        var tenantId = "club-sama";
        var botPhone = "34111222333";
        var messageId = $"wamid.{Guid.NewGuid()}";

        using var session = fixture.Host.Services.GetRequiredService<IDocumentStore>().LightweightSession(tenantId);

        session.Store(new TenantProfile { Id = tenantId, BotPhoneNumberId = botPhone });

        // Simulate that the webhook was already processed in the past and the projection saved it
        session.Store(new ProcessedMessage { Id = messageId, TenantId = tenantId, BotPhoneNumberId = botPhone, ProcessedAt = DateTimeOffset.UtcNow.AddMinutes(-5) });
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        var duplicateWebhookCommand = new ProcessWhatsAppMessage(
            MessageId: messageId,
            PhoneNumber: "34999888777",
            Text: "This message is a retry from the Meta server",
            BotPhoneNumberId: botPhone,
            Timestamp: DateTimeOffset.UtcNow
        );

        // Act
        var trackedSession = await fixture.Host.InvokeMessageAndWaitAsync(duplicateWebhookCommand);

        // Assert 1: No new events should have been saved in the user's stream
        var streamEvents = await session.Events.FetchStreamAsync(duplicateWebhookCommand.PhoneNumber, token: TestContext.Current.CancellationToken);
        streamEvents.Should().BeEmpty("Because the message ID was duplicated, the handler should have aborted before appending events.");

        // Assert 2: Wolverine should NOT have published ANYTHING to the AI bot bus
        var dispatchedEvents = trackedSession.Executed.MessagesOf<MessageReceived>();
        dispatchedEvents.Should().BeEmpty("No events should be published to the bus for duplicate incoming webhooks.");
    }

    [Fact]
    public async Task GivenMoreThanMaxMessagesPerMinute_WhenHandlerExecutes_ThenItDropsExcessMessages()
    {
        // Arrange
        var tenantId = "rate-limit-tenant";
        var botPhone = "34000000000";
        var spammerPhone = "34666555444";

        using var session = fixture.Host.Services.GetRequiredService<IDocumentStore>().LightweightSession(tenantId);
        session.Store(new TenantProfile { Id = tenantId, BotPhoneNumberId = botPhone });

        // The tracker id is an HMAC of tenant + phone. A document stored under the raw phone must not match.
        PhoneRateKey.TryHash(tenantId, spammerPhone, IntegrationAppFixture.PhoneHashKey, out var trackerId).Should().BeTrue();
        session.Store(new WhatsAppRateLimitTracker
        {
            Id = trackerId,
            MessageCount = 25,
            WindowResetTime = DateTimeOffset.UtcNow.AddMinutes(1)
        });
        session.Store(new WhatsAppRateLimitTracker { Id = spammerPhone, MessageCount = 3, WindowResetTime = DateTimeOffset.UtcNow.AddMinutes(1) });
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        var spamCommand = new ProcessWhatsAppMessage(
            MessageId: $"wamid.{Guid.NewGuid()}",
            BotPhoneNumberId: botPhone,
            PhoneNumber: spammerPhone,
            Text: "This is the 26th message, it should be dropped!",
            Timestamp: DateTimeOffset.UtcNow
        );

        // Act
        var trackedSession = await fixture.Host.InvokeMessageAndWaitAsync(spamCommand);

        // Assert 1: No events should have been appended to the event stream
        var streamEvents = await session.Events.FetchStreamAsync(spammerPhone, token: TestContext.Current.CancellationToken);
        streamEvents.Should().BeEmpty("The rate limit was reached, so the message must be dropped before appending events.");

        // Assert 2: Wolverine should not have dispatched the MessageReceived event downstream
        var dispatchedEvents = trackedSession.Executed.MessagesOf<MessageReceived>();
        dispatchedEvents.Should().BeEmpty("No events should be published when the spam shield is triggered.");

        // Assert 3: Ensure the tracker incremented to 26 and was saved (locking the spammer out)
        var tracker = await session.LoadAsync<WhatsAppRateLimitTracker>(trackerId, token: TestContext.Current.CancellationToken);
        tracker.Should().NotBeNull();
        tracker!.MessageCount.Should().Be(26, "The tracker must increment and save the blocked attempt.");
        var plain = await session.LoadAsync<WhatsAppRateLimitTracker>(spammerPhone, token: TestContext.Current.CancellationToken);
        plain.Should().BeNull();
    }

    [Fact]
    public async Task MissingHashKey_ThrowsSoTheMessageIsRetried()
    {
        var options = fixture.Host.Services.GetRequiredService<IOptions<UsageOptions>>();
        var saved = options.Value.PhoneHashKey;
        options.Value.PhoneHashKey = string.Empty;
        try
        {
            var bot = "6" + Guid.NewGuid().ToString("N")[..12];
            await fixture.SeedTenantAsync("hash-" + Guid.NewGuid().ToString("N")[..8], bot);
            var act = () => fixture.Host.InvokeMessageAndWaitAsync(new ProcessWhatsAppMessage($"wamid.{Guid.NewGuid()}", bot, "34600111222", "Hola", DateTimeOffset.UtcNow));
            await act.Should().ThrowAsync<InvalidOperationException>();
        }
        finally
        {
            options.Value.PhoneHashKey = saved;
        }
    }
}