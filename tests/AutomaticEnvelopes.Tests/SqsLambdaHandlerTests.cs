using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using AutomaticEnvelopes.Api;
using AutomaticEnvelopes.Api.Core.Events;
using AutomaticEnvelopes.Api.Features.Chat;
using AutomaticEnvelopes.Api.Features.WhatsAppWebhook.Models;
using AwesomeAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using System.Text.Json;
using static Amazon.Lambda.SQSEvents.SQSEvent;

namespace AutomaticEnvelopes.Tests;

[Collection("Integration")]
public class SqsLambdaHandlerIntegrationTests(IntegrationAppFixture fixture) : IAsyncLifetime
{
    private SqsLambdaHandler _handler = null!;

    public Task InitializeAsync()
    {
        var bus = fixture.Host.Services.GetRequiredService<Wolverine.IMessageBus>();
        var logger = fixture.Host.Services.GetRequiredService<ILogger<SqsLambdaHandler>>();

        _handler = new SqsLambdaHandler(bus, logger);
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task FunctionHandler_ValidWhatsAppMessage_SavesEventToMarten()
    {
        // Arrange
        var tenantId = $"tenant-sqs-{Guid.NewGuid():N}";
        var botPhoneId = "101010";
        var userPhone = $"346{Random.Shared.Next(10000000, 99999999)}";

        await fixture.SeedTenantAsync(tenantId, botPhoneId);

        var msg = new ProcessWhatsAppMessage("wamid.SQS1", botPhoneId, userPhone, "Hello from SQS", DateTimeOffset.UtcNow);
        var sqsEvent = CreateSqsEvent(JsonSerializer.Serialize(msg), "arn:aws:sqs:eu-west-1:123:whatsapp-queue", "sqs-msg-1");

        // Act
        var result = await _handler.FunctionHandler(sqsEvent, Mock.Of<ILambdaContext>());

        // Assert:
        result.BatchItemFailures.Should().BeEmpty();

        // Assert: 
        using var session = fixture.Host.Services.GetRequiredService<IDocumentStore>().LightweightSession(tenantId);
        var streamEvents = await session.Events.FetchStreamAsync(userPhone);

        var receivedEvent = streamEvents.Select(e => e.Data).OfType<MessageReceived>().FirstOrDefault();
        receivedEvent.Should().NotBeNull("El handler de SQS debería haber invocado el bus, procesado el mensaje y guardado en PostgreSQL.");
        receivedEvent!.Text.Should().Be("Hello from SQS");
    }

    [Fact]
    public async Task FunctionHandler_WhatsAppMessageWithoutBotId_LogsWarningAndIgnores()
    {
        // Arrange
        var msg = new ProcessWhatsAppMessage("wamid.123", "", "34600", "Hello", DateTimeOffset.UtcNow); // Bot ID vacío
        var sqsEvent = CreateSqsEvent(JsonSerializer.Serialize(msg), "arn:aws:sqs:whatsapp", "msg-id");

        // Act
        var result = await _handler.FunctionHandler(sqsEvent, Mock.Of<ILambdaContext>());

        // Assert
        result.BatchItemFailures.Should().BeEmpty();
    }

    [Fact]
    public async Task FunctionHandler_ValidSystemMessage_ResolvesSagaInMarten()
    {
        // Arrange
        var tenantId = $"tenant-sys-{Guid.NewGuid():N}";
        var botPhoneId = "202020";
        var userPhone = $"346{Random.Shared.Next(10000000, 99999999)}";

        await fixture.SeedTenantAsync(tenantId, botPhoneId);

        using (var setupSession = fixture.Host.Services.GetRequiredService<IDocumentStore>().LightweightSession())
        {
            var saga = new ChatDebounceSaga
            {
                Id = userPhone,
                TenantId = tenantId,
                BotPhoneNumberId = botPhoneId,
                CombinedText = "Pending text"
            };
            setupSession.Store(saga);
            await setupSession.SaveChangesAsync();
        }

        var msg = new ChatWindowExpired(userPhone);
        var sqsEvent = CreateSqsEvent(JsonSerializer.Serialize(msg), "arn:aws:sqs:eu-west-1:123:automatic-envelopes-system-queue", "sqs-msg-2");

        // Act
        var result = await _handler.FunctionHandler(sqsEvent, Mock.Of<ILambdaContext>());

        // Assert
        result.BatchItemFailures.Should().BeEmpty();

        using var session = fixture.Host.Services.GetRequiredService<IDocumentStore>().LightweightSession();
        var deletedSaga = await session.LoadAsync<ChatDebounceSaga>(userPhone);

        deletedSaga.Should().BeNull("La saga debe haber sido procesada y eliminada (MarkCompleted) por el evento del sistema.");
    }

    [Fact]
    public async Task FunctionHandler_SystemMessageMissingPhone_LogsWarningAndIgnores()
    {
        // Arrange
        var msg = new ChatWindowExpired(""); // Teléfono vacío
        var sqsEvent = CreateSqsEvent(JsonSerializer.Serialize(msg), "arn:aws:sqs:automatic-envelopes-system-queue", "sys-msg-id");

        // Act
        var result = await _handler.FunctionHandler(sqsEvent, Mock.Of<ILambdaContext>());

        // Assert
        result.BatchItemFailures.Should().BeEmpty();
    }

    [Fact]
    public async Task FunctionHandler_InvalidJson_AddsToBatchFailures()
    {
        // Arrange
        var sqsEvent = CreateSqsEvent("{ corrupt_json: ", "arn:aws:sqs:whatsapp", "bad-msg-id");

        // Act
        var result = await _handler.FunctionHandler(sqsEvent, Mock.Of<ILambdaContext>());

        // Assert
        result.BatchItemFailures.Should().ContainSingle();
        result.BatchItemFailures.First().ItemIdentifier.Should().Be("bad-msg-id");
    }

    [Fact]
    public async Task FunctionHandler_EmptyRecords_DoesNothing()
    {
        // Arrange
        var sqsEvent = new SQSEvent { Records = [] };

        // Act
        var result = await _handler.FunctionHandler(sqsEvent, Mock.Of<ILambdaContext>());

        // Assert
        result.BatchItemFailures.Should().BeEmpty();
    }

    // ==========================================
    // HELPER
    // ==========================================
    private static SQSEvent CreateSqsEvent(string body, string arn, string messageId = "test-msg-id")
    {
        return new SQSEvent
        {
            Records = [
                new SQSMessage
                {
                    MessageId = messageId,
                    Body = body,
                    EventSourceArn = arn
                }
            ]
        };
    }
}