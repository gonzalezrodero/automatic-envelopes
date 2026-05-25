using AwesomeAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using SamaBot.Api.Core.Events;
using SamaBot.Api.Features.Tenancy; // Añadido para TenantProfile
using SamaBot.Api.Features.WhatsAppWebhook;
using SamaBot.Tests.Extensions;

namespace SamaBot.Tests.Features.WhatsAppWebhook;

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
            BotPhoneNumberId: botPhoneId, // El comando viene con el ID de Meta
            PhoneNumber: "34999111222",
            Text: "Pure Handler Text",
            Timestamp: DateTimeOffset.UtcNow
        );

        // Act: Directly invoke the message bypassing the HTTP layer
        await fixture.Host.InvokeMessageAndWaitAsync(command);

        // Assert: The message was correctly sourced in the database
        using var session = fixture.Host.Services.GetRequiredService<IDocumentStore>().LightweightSession(tenantSlug);

        var streamEvents = await session.Events.FetchStreamAsync("34999111222");

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

        var command = new ProcessWhatsAppMessage("wamid.DUP", botPhoneId, "34999111222", "Texto", DateTimeOffset.UtcNow);

        // Act: Send the same message twice to simulate Meta webhook retries
        await fixture.Host.InvokeMessageAndWaitAsync(command);
        await fixture.Host.InvokeMessageAndWaitAsync(command);

        // Assert: It should only exist once in the stream
        using var session = fixture.Host.Services.GetRequiredService<IDocumentStore>().LightweightSession(tenantSlug);

        var streamEvents = await session.Events.FetchStreamAsync("34999111222");

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

        // Simulamos que el webhook ya se procesó en el pasado y la proyección lo guardó
        session.Store(new ProcessedMessage { Id = messageId, TenantId = tenantId, BotPhoneNumberId = botPhone, ProcessedAt = DateTimeOffset.UtcNow.AddMinutes(-5) });
        await session.SaveChangesAsync();

        var duplicateWebhookCommand = new ProcessWhatsAppMessage(
                    MessageId: messageId,
                    PhoneNumber: "34999888777",
                    Text: "Este mensaje es un reintento del servidor de Meta",
                    BotPhoneNumberId: botPhone,
                    Timestamp: DateTimeOffset.UtcNow
                );

        // Act
        var trackedSession = await fixture.Host.InvokeMessageAndWaitAsync(duplicateWebhookCommand);

        // Assert 1: No se deben haber guardado eventos nuevos en el stream del usuario
        var streamEvents = await session.Events.FetchStreamAsync(duplicateWebhookCommand.PhoneNumber);
        streamEvents.Should().BeEmpty("Because the message ID was duplicated, the handler should have aborted before appending events.");

        // Assert 2: Wolverine no debe haber publicado NADA hacia el bot de IA
        var dispatchedEvents = trackedSession.Executed.MessagesOf<MessageReceived>();
        dispatchedEvents.Should().BeEmpty("No events should be published to the bus for duplicate incoming webhooks.");
    }
}