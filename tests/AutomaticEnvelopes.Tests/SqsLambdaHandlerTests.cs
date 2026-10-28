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

    ValueTask IAsyncLifetime.InitializeAsync()
    {
        var bus = fixture.Host.Services.GetRequiredService<Wolverine.IMessageBus>();
        var logger = fixture.Host.Services.GetRequiredService<ILogger<SqsLambdaHandler>>();

        _handler = new SqsLambdaHandler(bus, logger);
        return ValueTask.CompletedTask;
    }

    ValueTask IAsyncDisposable.DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

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
        var streamEvents = await session.Events.FetchStreamAsync(userPhone, token: TestContext.Current.CancellationToken);

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
    public async Task FunctionHandler_SystemMessage_ResolvesSagaInMarten()
    {
        // Arrange
        var tenantId = $"tenant-sys-{Guid.NewGuid():N}";
        var botPhoneId = "202020";
        var userPhone = $"346{Random.Shared.Next(10000000, 99999999)}";
        var sagaId = ChatSagaIds.For(tenantId, userPhone);

        await fixture.SeedTenantAsync(tenantId, botPhoneId);

        using (var setupSession = fixture.Host.Services.GetRequiredService<IDocumentStore>().LightweightSession())
        {
            var saga = new ChatDebounceSaga
            {
                Id = sagaId,
                PhoneNumber = userPhone,
                TenantId = tenantId,
                BotPhoneNumberId = botPhoneId,
                CombinedText = "Pending text"
            };
            setupSession.Store(saga);
            await setupSession.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var msg = new ChatWindowExpired(sagaId, default);
        var sqsEvent = CreateSqsEvent(JsonSerializer.Serialize(msg), "arn:aws:sqs:eu-west-1:123:automatic-envelopes-system-queue", "sqs-msg-2");

        // Act
        var result = await _handler.FunctionHandler(sqsEvent, Mock.Of<ILambdaContext>());

        // Assert
        result.BatchItemFailures.Should().BeEmpty();

        using var session = fixture.Host.Services.GetRequiredService<IDocumentStore>().LightweightSession();
        var deletedSaga = await session.LoadAsync<ChatDebounceSaga>(sagaId, token: TestContext.Current.CancellationToken);

        deletedSaga.Should().BeNull("La saga debe haber sido procesada y eliminada (MarkCompleted) por el evento del sistema.");
    }

    [Fact]
    public async Task FunctionHandler_TenantIdContainingTheOldSweepName_StillClosesTheWindow()
    {
        const string tenantId = "recover-stuck-chat-sagas";
        var botPhoneId = "303030";
        var userPhone = $"346{Random.Shared.Next(10000000, 99999999)}";
        var sagaId = ChatSagaIds.For(tenantId, userPhone);
        var startedAt = DateTimeOffset.UtcNow;

        await fixture.SeedTenantAsync(tenantId, botPhoneId);
        using (var setup = fixture.Host.Services.GetRequiredService<IDocumentStore>().LightweightSession())
        {
            setup.Store(new ChatDebounceSaga
            {
                Id = sagaId,
                PhoneNumber = userPhone,
                TenantId = tenantId,
                BotPhoneNumberId = botPhoneId,
                CombinedText = "Hello",
                StartedAt = startedAt
            });
            await setup.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var body = JsonSerializer.Serialize(new ChatWindowExpired(sagaId, startedAt.UtcTicks));
        var sqsEvent = CreateSqsEvent(body, "arn:aws:sqs:eu-west-1:123:automatic-envelopes-system-queue", "kind-substr");

        var result = await _handler.FunctionHandler(sqsEvent, Mock.Of<ILambdaContext>());

        result.BatchItemFailures.Should().BeEmpty();
        using var session = fixture.Host.Services.GetRequiredService<IDocumentStore>().LightweightSession();
        var deleted = await session.LoadAsync<ChatDebounceSaga>(sagaId, TestContext.Current.CancellationToken);
        deleted.Should().BeNull();
    }

    [Fact]
    public async Task FunctionHandler_SystemMessageMissingPhone_LogsWarningAndIgnores()
    {
        // Arrange
        var msg = new ChatWindowExpired("", default);
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