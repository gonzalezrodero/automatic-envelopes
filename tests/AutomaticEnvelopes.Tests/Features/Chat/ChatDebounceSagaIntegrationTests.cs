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
        var sagaId = ChatSagaIds.For(tenantId, userPhone);
        using var querySession = fixture.Host.Services.GetRequiredService<IDocumentStore>().QuerySession();
        var sagaState = await querySession.LoadAsync<ChatDebounceSaga>(sagaId, TestContext.Current.CancellationToken);

        sagaState.Should().NotBeNull();
        sagaState!.CombinedText.Should().Be("Hello\nI need help\nwith my account");
        sagaState.TenantId.Should().Be(tenantId);
        sagaState.PhoneNumber.Should().Be(userPhone);

        // Act 3: Force the timeout to simulate the 10 seconds passing instantly
        var trackedSession = await fixture.Host.InvokeMessageAndWaitAsync(new ChatWindowExpired(sagaId));

        // Assert 2: Verify Wolverine published the final consolidated command to the AI Handler
        var aiCommand = trackedSession.Executed.MessagesOf<AnalyzeChatSession>().SingleOrDefault();
        aiCommand.Should().NotBeNull("The saga should have dispatched the AI command upon expiration.");
        aiCommand!.CombinedText.Should().Be("Hello\nI need help\nwith my account");

        // Assert 3: Verify the Saga was cleaned up from PostgreSQL
        var deletedSaga = await querySession.LoadAsync<ChatDebounceSaga>(sagaId, TestContext.Current.CancellationToken);
        deletedSaga.Should().BeNull("Marten should have deleted the saga row after completion.");
    }

    [Fact]
    public async Task GivenSamePhoneOnTwoTenants_WhenMessagesArriveInsideTheWindow_ThenEachSagaKeepsItsTenant()
    {
        var phone = $"346{Random.Shared.Next(10000000, 99999999)}";
        var tenantA = $"tenant-a-{Guid.NewGuid():N}";
        var tenantB = $"tenant-b-{Guid.NewGuid():N}";
        var botA = $"bot-a-{Guid.NewGuid():N}";
        var botB = $"bot-b-{Guid.NewGuid():N}";
        await fixture.SeedTenantAsync(tenantA, botA);
        await fixture.SeedTenantAsync(tenantB, botB);

        await fixture.Host.InvokeMessageAndWaitAsync(new MessageReceived("m1", phone, "Hello from A", tenantA, botA, DateTimeOffset.UtcNow));
        await fixture.Host.InvokeMessageAndWaitAsync(new MessageReceived("m2", phone, "Hello from B", tenantB, botB, DateTimeOffset.UtcNow));
        await fixture.Host.InvokeMessageAndWaitAsync(new MessageReceived("m3", phone, "Still A", tenantA, botA, DateTimeOffset.UtcNow));

        using var query = fixture.Host.Services.GetRequiredService<IDocumentStore>().QuerySession();
        var sagaA = await query.LoadAsync<ChatDebounceSaga>(ChatSagaIds.For(tenantA, phone), TestContext.Current.CancellationToken);
        var sagaB = await query.LoadAsync<ChatDebounceSaga>(ChatSagaIds.For(tenantB, phone), TestContext.Current.CancellationToken);

        sagaA.Should().NotBeNull();
        sagaA!.TenantId.Should().Be(tenantA);
        sagaA.PhoneNumber.Should().Be(phone);
        sagaA.CombinedText.Should().Be("Hello from A\nStill A");

        sagaB.Should().NotBeNull();
        sagaB!.TenantId.Should().Be(tenantB);
        sagaB.PhoneNumber.Should().Be(phone);
        sagaB.CombinedText.Should().Be("Hello from B");

        var trackedA = await fixture.Host.InvokeMessageAndWaitAsync(new ChatWindowExpired(sagaA.Id));
        var commandA = trackedA.Executed.MessagesOf<AnalyzeChatSession>().Single();
        commandA.TenantId.Should().Be(tenantA);
        commandA.PhoneNumber.Should().Be(phone);
        commandA.CombinedText.Should().Be("Hello from A\nStill A");

        var trackedB = await fixture.Host.InvokeMessageAndWaitAsync(new ChatWindowExpired(sagaB.Id));
        var commandB = trackedB.Executed.MessagesOf<AnalyzeChatSession>().Single();
        commandB.TenantId.Should().Be(tenantB);
        commandB.PhoneNumber.Should().Be(phone);
        commandB.CombinedText.Should().Be("Hello from B");
    }
}