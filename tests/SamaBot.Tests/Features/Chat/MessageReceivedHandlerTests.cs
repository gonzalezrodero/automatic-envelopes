using Amazon.BedrockRuntime.Model;
using AwesomeAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SamaBot.Api.Core.Events;
using SamaBot.Api.Features.Chat;
using SamaBot.Tests.Extensions;
using System.Text;

namespace SamaBot.Tests.Features.Chat;

[Collection("Integration")]
public class MessageReceivedHandlerTests(IntegrationAppFixture fixture)
{
    private const string PrivacyPolicyUrl = "https://static1.squarespace.com/static/5d774ba386ebf92cf9611ccf/t/65cb39917d01065ce0d02a07/1707817361861/POLITICA+DE+PRIVACIDAD.pdf";

    [Fact]
    public async Task GivenReceivedMessage_WhenHandlerRuns_ThenItGeneratesReplyAndAppendsToStream()
    {
        // Arrange
        fixture.BedrockClientMock.Invocations.Clear();

        var tenantId = "club-sama";
        var botPhone = "34111222333";
        var userPhone = "34888777666";

        await fixture.SeedTenantAsync(tenantId, botPhone, privacyPolicyUrl: PrivacyPolicyUrl);

        var incomingEvent = new MessageReceived(
            MessageId: "atomic.Chat1",
            PhoneNumber: userPhone,
            Text: "Quina és la contrasenya?",
            TenantId: tenantId,
            BotPhoneNumberId: botPhone,
            ReceivedAt: DateTimeOffset.UtcNow
        );

        // Act
        await fixture.Host.InvokeMessageAndWaitAsync(incomingEvent);

        // Assert
        using var session = fixture.Host.Services.GetRequiredService<IDocumentStore>().LightweightSession(tenantId);
        var streamEvents = await session.Events.FetchStreamAsync(userPhone);

        var replyGenerated = streamEvents.FirstOrDefault(e => e.Data is ReplyGenerated)?.Data as ReplyGenerated;

        replyGenerated.Should().NotBeNull("The handler should have appended a ReplyGenerated event to the stream.");

        replyGenerated!.MessageId.Should().Be("atomic.Chat1");
        replyGenerated.BotPhoneNumberId.Should().Be(botPhone);
        replyGenerated.PhoneNumber.Should().Be(userPhone);
        replyGenerated.TenantId.Should().Be(tenantId);

        fixture.BedrockClientMock.Verify(c => c.InvokeModelAsync(It.Is<InvokeModelRequest>(req =>
            VerifyPrivacyPolicyInjected(req)
        ), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GivenPreviousConversation_WhenHandlerRuns_ThenItUsesHistoryAndAppendsReply()
    {
        // Arrange
        fixture.BedrockClientMock.Invocations.Clear();

        var tenantId = "club-sama";
        var botPhone = "34111222333";
        var userPhone = "34999555111";

        await fixture.SeedTenantAsync(tenantId, botPhone, privacyPolicyUrl: PrivacyPolicyUrl);

        using var session = fixture.Host.Services.GetRequiredService<IDocumentStore>().LightweightSession(tenantId);

        // 1. Pre-populate the Marten Event Store
        session.Events.Append(userPhone, new MessageReceived("old.1", userPhone, "Hola", tenantId, botPhone, DateTimeOffset.UtcNow));
        session.Events.Append(userPhone, new ReplyGenerated("old.1", botPhone, userPhone, "¡Hola! Soy SamàBot.", tenantId));

        // This is the event that will trigger the handler, but we also save it so the handler reads it as context
        var incomingEvent = new MessageReceived("atomic.Chat2", userPhone, "¿Me recuerdas?", tenantId, botPhone, DateTimeOffset.UtcNow);
        session.Events.Append(userPhone, incomingEvent);
        await session.SaveChangesAsync();

        // Act
        await fixture.Host.InvokeMessageAndWaitAsync(incomingEvent);

        // Assert
        var streamEvents = await session.Events.FetchStreamAsync(userPhone);
        var replies = streamEvents.Select(e => e.Data).OfType<ReplyGenerated>().ToList();

        replies.Should().HaveCount(2);
        replies.Last().MessageId.Should().Be("atomic.Chat2");

        // Verify history is sent AND Privacy Policy is NOT injected since it is a returning user
        fixture.BedrockClientMock.Verify(c => c.InvokeModelAsync(It.Is<InvokeModelRequest>(req =>
            VerifyChatHistoryPayload(req) && !VerifyPrivacyPolicyInjected(req)
        ), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("BORRAR DATOS")]
    [InlineData("esborrar dades")]
    [InlineData("DELETE data")]
    public async Task GivenDeleteCommand_WhenHandlerRuns_ThenItSendsAckMessageAndTriggersDeleteCommand(string commandText)
    {
        // Arrange
        fixture.BedrockClientMock.Invocations.Clear();

        var tenantId = "club-sama";
        var botPhone = "34111222333";
        var userPhone = $"3477{Guid.NewGuid().ToString()[..7]}";

        await fixture.SeedTenantAsync(tenantId, botPhone, privacyPolicyUrl: PrivacyPolicyUrl);

        using var session = fixture.Host.Services.GetRequiredService<IDocumentStore>().LightweightSession(tenantId);

        session.Events.Append(userPhone, new MessageReceived("old.1", userPhone, "Hola", tenantId, botPhone, DateTimeOffset.UtcNow));
        await session.SaveChangesAsync();

        var incomingEvent = new MessageReceived(
            MessageId: "atomic.DeleteCmd",
            PhoneNumber: userPhone,
            Text: commandText,
            TenantId: tenantId,
            BotPhoneNumberId: botPhone,
            ReceivedAt: DateTimeOffset.UtcNow
        );

        // Act
        var trackedSession = await fixture.Host.InvokeMessageAndWaitAsync(incomingEvent);

        // Assert 1: Verify the Replies (ACK and Success) were executed internally
        var executedReplies = trackedSession.Executed.MessagesOf<ReplyGenerated>().ToList();

        executedReplies.Should().HaveCount(2, "The handler should send an ACK, and the background worker should send the final success message.");
        executedReplies.Should().Contain(x => x.Text.Contains("Estamos borrando tu historial"), "The initial ACK message should be sent.");
        executedReplies.Should().Contain(x => x.Text.Contains("eliminados de forma segura"), "The final success message should be sent by the worker.");

        // Assert 2: Verify the background worker command was triggered
        var executedCommands = trackedSession.Executed.MessagesOf<DeleteChatHistoryCommand>().ToList();
        executedCommands.Should().ContainSingle("The handler should have delegated the actual deletion to the background worker.");

        // Assert 3: Verify the Hard Delete actually happened
        var streamEvents = await session.Events.FetchStreamAsync(userPhone);
        streamEvents.Should().BeEmpty("The background worker should have hard-deleted the stream in the same transaction cascade.");
    }

    private static bool VerifyChatHistoryPayload(InvokeModelRequest request)
    {
        var requestJson = Encoding.UTF8.GetString(request.Body.ToArray());
        return requestJson.Contains("Hola") &&
               requestJson.Contains("¡Hola! Soy SamàBot.") &&
               requestJson.Contains("¿Me recuerdas?");
    }

    private static bool VerifyPrivacyPolicyInjected(InvokeModelRequest request)
    {
        var requestJson = Encoding.UTF8.GetString(request.Body.ToArray());
        return requestJson.Contains("POLITICA+DE+PRIVACIDAD.pdf");
    }
}