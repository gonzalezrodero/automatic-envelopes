using AwesomeAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using SamaBot.Api.Core.Events;
using SamaBot.Api.Features.WhatsAppWebhook;

namespace SamaBot.Tests.Features.WhatsAppWebhook;

[Collection("Integration")]
public class ProcessedMessageProjectionTests(IntegrationAppFixture fixture)
{
    [Fact]
    public async Task GivenMessageReceived_WhenProjected_ThenItCreatesProcessedMessageWithoutPhoneNumber()
    {
        // Arrange
        var tenantId = "club-sama";
        var messageId = $"wamid.{Guid.NewGuid()}";
        var phoneNumber = "34666555444";

        using var session = fixture.Host.Services.GetRequiredService<IDocumentStore>().LightweightSession(tenantId);

        var @event = new MessageReceived(
            MessageId: messageId,
            PhoneNumber: phoneNumber,
            Text: "Hola bot",
            TenantId: tenantId,
            BotPhoneNumberId: "34111222333",
            ReceivedAt: DateTimeOffset.UtcNow
        );

        // Act
        session.Events.Append(phoneNumber, @event);
        await session.SaveChangesAsync();

        // Assert
        var projectedDoc = await session.LoadAsync<ProcessedMessage>(messageId);

        projectedDoc.Should().NotBeNull("The projection should create a document using the MessageId.");
        projectedDoc!.Id.Should().Be(messageId);
        projectedDoc.TenantId.Should().Be(tenantId);

        var rawJson = await session.Json.FindByIdAsync<ProcessedMessage>(messageId);
        rawJson.Should().NotContain(phoneNumber, "The projected document must not contain PII (Phone Number) to comply with GDPR data minimization.");
    }
}