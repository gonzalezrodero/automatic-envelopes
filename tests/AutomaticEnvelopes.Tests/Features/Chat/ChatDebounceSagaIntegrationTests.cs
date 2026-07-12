using AutomaticEnvelopes.Api.Core.Events;
using AutomaticEnvelopes.Api.Features.Chat;
using AutomaticEnvelopes.Tests.Extensions;
using AwesomeAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace AutomaticEnvelopes.Tests.Features.Chat;

[Collection("Integration")]
public class ChatDebounceSagaIntegrationTests(IntegrationAppFixture fixture)
{
    [Fact]
    public async Task GivenRapidSequentialMessages_WhenHandled_ThenSagaCombinesThemInPostgreSQL()
    {
        // Arrange
        var tenantId = "test-tenant-saga";
        var botPhoneId = "100000";
        var userPhone = "34777888999";

        await fixture.SeedTenantAsync(tenantId, botPhoneId);

        var msg1 = new MessageReceived("msg1", userPhone, "Hello", tenantId, botPhoneId, DateTimeOffset.UtcNow);
        var msg2 = new MessageReceived("msg2", userPhone, "I need help", tenantId, botPhoneId, DateTimeOffset.UtcNow);
        var msg3 = new MessageReceived("msg3", userPhone, "with my account", tenantId, botPhoneId, DateTimeOffset.UtcNow);

        // Act 1: Send the first message. Wolverine will create the Saga.
        await fixture.Host.InvokeMessageAndWaitAsync(msg1);

        // Act 2: Send the follow-up messages. Wolverine will append them.
        await fixture.Host.InvokeMessageAndWaitAsync(msg2);
        await fixture.Host.InvokeMessageAndWaitAsync(msg3);

        // Assert 1: Verify the state is correctly grouped inside PostgreSQL
        using var querySession = fixture.Host.Services.GetRequiredService<IDocumentStore>().QuerySession();
        var sagaState = await querySession.LoadAsync<ChatDebounceSaga>(userPhone);

        sagaState.Should().NotBeNull();
        sagaState!.CombinedText.Should().Be("Hello\nI need help\nwith my account");
        sagaState.TenantId.Should().Be(tenantId);

        // Act 3: Force the timeout to simulate the 10 seconds passing instantly
        var trackedSession = await fixture.Host.InvokeMessageAndWaitAsync(new ChatWindowExpired(userPhone));

        // Assert 2: Verify Wolverine published the final consolidated command to the AI Handler
        var aiCommand = trackedSession.Sent.MessagesOf<AnalyzeChatSession>().SingleOrDefault();
        aiCommand.Should().NotBeNull("The saga should have dispatched the AI command upon expiration.");
        aiCommand!.CombinedText.Should().Be("Hello\nI need help\nwith my account");

        // Assert 3: Verify the Saga was cleaned up from PostgreSQL
        var deletedSaga = await querySession.LoadAsync<ChatDebounceSaga>(userPhone);
        deletedSaga.Should().BeNull("Marten should have deleted the saga row after completion.");
    }
}