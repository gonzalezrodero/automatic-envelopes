using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using AutomaticEnvelopes.Api.Core.Events;
using AutomaticEnvelopes.Api.Features.Chat;
using AutomaticEnvelopes.Api.Features.Chat.Models;
using AutomaticEnvelopes.Tests.Extensions;
using AwesomeAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace AutomaticEnvelopes.Tests.Features.Chat;

[Collection("Integration")]
public class ChatSessionHandlerTests(IntegrationAppFixture fixture)
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

        // FIX: Simular que el Webhook guardó el mensaje en BD antes de que el Handler de la IA se ejecute
        using var arrangeSession = fixture.Host.Services.GetRequiredService<IDocumentStore>().LightweightSession(tenantId);
        arrangeSession.Events.Append(userPhone, new MessageReceived("wamid.1", userPhone, "Quina és la contrasenya?", tenantId, botPhone, DateTimeOffset.UtcNow));
        await arrangeSession.SaveChangesAsync();

        var incomingCommand = new AnalyzeChatSession(
            PhoneNumber: userPhone,
            TenantId: tenantId,
            BotPhoneNumberId: botPhone,
            CombinedText: "Quina és la contrasenya?"
        );

        // Act
        await fixture.Host.InvokeMessageAndWaitAsync(incomingCommand);

        // Assert 1: Stream state
        using var session = fixture.Host.Services.GetRequiredService<IDocumentStore>().LightweightSession(tenantId);
        var streamEvents = await session.Events.FetchStreamAsync(userPhone);

        var replyGenerated = streamEvents.FirstOrDefault(e => e.Data is ReplyGenerated)?.Data as ReplyGenerated;

        replyGenerated.Should().NotBeNull("The handler should have appended a ReplyGenerated event to the stream.");
        replyGenerated!.MessageId.Should().StartWith("wamid.grouped.");
        replyGenerated.BotPhoneNumberId.Should().Be(botPhone);
        replyGenerated.PhoneNumber.Should().Be(userPhone);
        replyGenerated.TenantId.Should().Be(tenantId);

        // Assert 2: Verify Bedrock call and Privacy Policy
        fixture.BedrockClientMock.Verify(c => c.ConverseAsync(It.IsAny<ConverseRequest>(), It.IsAny<CancellationToken>()), Times.Once);

        var request = (ConverseRequest)fixture.BedrockClientMock.Invocations
            .First(i => i.Method.Name == nameof(IAmazonBedrockRuntime.ConverseAsync))
            .Arguments[0];

        // Safely extract system prompt text guarding against nulls
        var systemPrompt = request.System != null ? string.Join(" ", request.System.Select(s => s.Text)) : "";
        systemPrompt.Should().Contain("POLITICA+DE+PRIVACIDAD.pdf", "A new user should receive the privacy policy injection.");
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

        // 1. Pre-populate the Marten Event Store with OLD history
        session.Events.Append(userPhone, new MessageReceived("old.1", userPhone, "Hola", tenantId, botPhone, DateTimeOffset.UtcNow));
        session.Events.Append(userPhone, new ReplyGenerated("old.1", botPhone, userPhone, "¡Hola! Soy SamàBot.", tenantId));

        // FIX: Pre-populate the CURRENT message that the Webhook just received
        session.Events.Append(userPhone, new MessageReceived("new.2", userPhone, "¿Me recuerdas?", tenantId, botPhone, DateTimeOffset.UtcNow));
        await session.SaveChangesAsync();

        var incomingCommand = new AnalyzeChatSession(
            PhoneNumber: userPhone,
            TenantId: tenantId,
            BotPhoneNumberId: botPhone,
            CombinedText: "¿Me recuerdas?"
        );

        // Act
        await fixture.Host.InvokeMessageAndWaitAsync(incomingCommand);

        // Assert 1: Stream State
        var streamEvents = await session.Events.FetchStreamAsync(userPhone);
        var replies = streamEvents.Select(e => e.Data).OfType<ReplyGenerated>().ToList();

        replies.Should().HaveCount(2);
        replies.Last().MessageId.Should().StartWith("wamid.grouped.");

        // Assert 2: Verify Bedrock call
        fixture.BedrockClientMock.Verify(c => c.ConverseAsync(It.IsAny<ConverseRequest>(), It.IsAny<CancellationToken>()), Times.Once);

        // Extract the actual request sent to AWS
        var request = (ConverseRequest)fixture.BedrockClientMock.Invocations
            .First(i => i.Method.Name == nameof(IAmazonBedrockRuntime.ConverseAsync))
            .Arguments[0];

        // Assert 3: Returning users should NOT get the privacy policy again
        var systemPrompt = request.System != null ? string.Join(" ", request.System.Select(s => s.Text)) : "";
        systemPrompt.Should().NotContain("POLITICA+DE+PRIVACIDAD.pdf", "Returning users should not receive the privacy policy repeatedly.");

        // Assert 4: Chat History must be properly loaded
        var allText = request.Messages != null ? string.Join(" ", request.Messages.SelectMany(m => m.Content).Select(c => c.Text)) : "";

        allText.Should().Contain("Hola");
        allText.Should().Contain("SamàBot");
        allText.Should().Contain("recuerdas");
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
        // FIX: Add the delete command message to DB
        session.Events.Append(userPhone, new MessageReceived("new.delete", userPhone, commandText, tenantId, botPhone, DateTimeOffset.UtcNow));
        await session.SaveChangesAsync();

        var incomingCommand = new AnalyzeChatSession(
            PhoneNumber: userPhone,
            TenantId: tenantId,
            BotPhoneNumberId: botPhone,
            CombinedText: commandText
        );

        // Act
        var trackedSession = await fixture.Host.InvokeMessageAndWaitAsync(incomingCommand);

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
}