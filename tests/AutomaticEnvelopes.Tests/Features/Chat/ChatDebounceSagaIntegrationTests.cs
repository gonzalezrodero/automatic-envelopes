using AutomaticEnvelopes.Api.Core.Events;
using AutomaticEnvelopes.Api.Features.Chat;
using AutomaticEnvelopes.Tests.Extensions;
using AwesomeAssertions;
using JasperFx;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;

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
        var trackedSession = await fixture.Host.InvokeMessageAndWaitAsync(
            new ChatWindowExpired(sagaId, sagaState!.StartedAt.UtcTicks));

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

        var trackedA = await fixture.Host.InvokeMessageAndWaitAsync(new ChatWindowExpired(sagaA.Id, sagaA.StartedAt.UtcTicks));
        var commandA = trackedA.Executed.MessagesOf<AnalyzeChatSession>().Single();
        commandA.TenantId.Should().Be(tenantA);
        commandA.PhoneNumber.Should().Be(phone);
        commandA.CombinedText.Should().Be("Hello from A\nStill A");

        var trackedB = await fixture.Host.InvokeMessageAndWaitAsync(new ChatWindowExpired(sagaB.Id, sagaB.StartedAt.UtcTicks));
        var commandB = trackedB.Executed.MessagesOf<AnalyzeChatSession>().Single();
        commandB.TenantId.Should().Be(tenantB);
        commandB.PhoneNumber.Should().Be(phone);
        commandB.CombinedText.Should().Be("Hello from B");
    }

    [Fact]
    public async Task GivenTwoExpiryDeliveriesAtOnce_WhenBothRun_ThenOneReplyIsSent()
    {
        var (tenantId, _, phone, sagaId) = await OpenWindowAsync("Hello");
        var startedAt = await StartedAtAsync(sagaId);
        var expired = new ChatWindowExpired(sagaId, startedAt);

        await Task.WhenAll(Invoke(expired), Invoke(expired));

        (await ReplyCountAsync(tenantId, phone)).Should().Be(1);
        (await LoadSagaAsync(sagaId)).Should().BeNull();
    }

    [Fact]
    public async Task GivenTheReplyFails_WhenTheExpiryIsRetried_ThenTheSagaIsStillThereUntilItSucceeds()
    {
        var (tenantId, _, phone, sagaId) = await OpenWindowAsync("Hello");
        var startedAt = await StartedAtAsync(sagaId);
        var expired = new ChatWindowExpired(sagaId, startedAt);
        fixture.BeforeConverse = _ => throw new ConcurrencyException("bedrock");
        try
        {
            var act = () => Invoke(expired);
            await act.Should().ThrowAsync<ConcurrencyException>();
            (await LoadSagaAsync(sagaId)).Should().NotBeNull();
            (await LoadMarkerAsync(sagaId)).Should().BeNull();
            (await ReplyCountAsync(tenantId, phone)).Should().Be(0);
        }
        finally
        {
            fixture.BeforeConverse = null;
        }

        await Invoke(expired);

        (await LoadSagaAsync(sagaId)).Should().BeNull();
        (await ReplyCountAsync(tenantId, phone)).Should().Be(1);
    }

    [Fact]
    public async Task GivenAMessageSavedDuringExpiry_WhenTheWindowCloses_ThenThatMessageIsNotLost()
    {
        var (tenantId, botPhoneId, phone, sagaId) = await OpenWindowAsync("Hello");
        var startedAt = await StartedAtAsync(sagaId);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.BeforeConverse = async ct =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(ct);
        };

        try
        {
            var expiry = Invoke(new ChatWindowExpired(sagaId, startedAt));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
            var append = Invoke(
                new MessageReceived("late", phone, "World", tenantId, botPhoneId, DateTimeOffset.UtcNow));
            await Task.Delay(300, TestContext.Current.CancellationToken);
            release.TrySetResult();
            await Task.WhenAll(expiry, append);
        }
        finally
        {
            release.TrySetResult();
            fixture.BeforeConverse = null;
        }

        var saga = await LoadSagaAsync(sagaId);
        var seenByTheModel = fixture.ConverseTexts.Any(text => text.Contains("World", StringComparison.Ordinal));
        var keptOnTheSaga = saga?.CombinedText.Contains("World", StringComparison.Ordinal) == true;
        (seenByTheModel || keptOnTheSaga).Should().BeTrue();
    }

    [Fact]
    public async Task GivenALateExpiryFromThePreviousWindow_WhenANewWindowIsOpen_ThenTheNewWindowStaysOpen()
    {
        var (tenantId, botPhoneId, phone, sagaId) = await OpenWindowAsync("Hello");
        var firstTicks = await StartedAtAsync(sagaId);
        await Invoke(new ChatWindowExpired(sagaId, firstTicks));
        (await ReplyCountAsync(tenantId, phone)).Should().Be(1);

        await Invoke(
            new MessageReceived("next", phone, "A new question", tenantId, botPhoneId, DateTimeOffset.UtcNow));
        await Invoke(new ChatWindowExpired(sagaId, firstTicks));

        var reopened = await LoadSagaAsync(sagaId);
        reopened.Should().NotBeNull();
        reopened!.CombinedText.Should().Be("A new question");
        (await ReplyCountAsync(tenantId, phone)).Should().Be(1);

        await Invoke(new ChatWindowExpired(sagaId, reopened.StartedAt.UtcTicks));
        (await LoadSagaAsync(sagaId)).Should().BeNull();
        (await ReplyCountAsync(tenantId, phone)).Should().Be(2);
    }

    [Fact]
    public async Task GivenAnOverdueSaga_WhenSomeoneElseMessagesAndTheLeaseIsDueAgain_ThenItIsRepublished()
    {
        var tenantId = $"tenant-sweep-{Guid.NewGuid():N}";
        var botPhoneId = $"bot-{Guid.NewGuid():N}";
        var stalePhone = $"346{Random.Shared.Next(10000000, 99999999)}";
        var otherPhone = $"346{Random.Shared.Next(10000000, 99999999)}";
        await fixture.SeedTenantAsync(tenantId, botPhoneId);
        using (var session = Store().LightweightSession())
        {
            session.Delete<ChatDebounceMarker>(ChatDebounceMarker.RecoveryId);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var staleId = ChatSagaIds.For(tenantId, stalePhone);
        var freshId = ChatSagaIds.For(tenantId, $"346{Random.Shared.Next(10000000, 99999999)}");
        await StoreSagaAsync(staleId, tenantId, botPhoneId, stalePhone, "Still waiting", DateTimeOffset.UtcNow.AddMinutes(-30));
        await StoreSagaAsync(freshId, tenantId, botPhoneId, freshId.Split(':')[1], "Just arrived", DateTimeOffset.UtcNow);

        var first = await fixture.Host.InvokeMessageAndWaitAsync(
            new MessageReceived("other", otherPhone, "Anyone there", tenantId, botPhoneId, DateTimeOffset.UtcNow));
        var republished = first.Sent.MessagesOf<ChatWindowExpired>().Should().ContainSingle(expired => expired.SagaId == staleId).Subject;
        await fixture.Host.InvokeMessageAndWaitAsync(republished);

        (await LoadSagaAsync(staleId)).Should().BeNull();
        (await LoadSagaAsync(freshId)).Should().NotBeNull();
        (await ReplyCountAsync(tenantId, stalePhone)).Should().Be(1);

        var secondPhone = $"346{Random.Shared.Next(10000000, 99999999)}";
        var secondId = ChatSagaIds.For(tenantId, secondPhone);
        await StoreSagaAsync(secondId, tenantId, botPhoneId, secondPhone, "Also waiting", DateTimeOffset.UtcNow.AddMinutes(-30));
        using (var session = Store().LightweightSession())
        {
            var lease = await session.LoadAsync<ChatDebounceMarker>(ChatDebounceMarker.RecoveryId, TestContext.Current.CancellationToken);
            lease!.NotBefore = DateTimeOffset.UtcNow.AddMinutes(-1);
            session.Store(lease);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var second = await fixture.Host.InvokeMessageAndWaitAsync(
            new MessageReceived("other-2", otherPhone, "Still anyone", tenantId, botPhoneId, DateTimeOffset.UtcNow));
        var again = second.Sent.MessagesOf<ChatWindowExpired>().Should().ContainSingle(expired => expired.SagaId == secondId).Subject;
        await fixture.Host.InvokeMessageAndWaitAsync(again);

        (await LoadSagaAsync(secondId)).Should().BeNull();
        (await ReplyCountAsync(tenantId, secondPhone)).Should().Be(1);
    }

    private IMessageBus Bus() => fixture.Host.Services.GetRequiredService<IMessageBus>();

    private Task Invoke(object message) => Bus().InvokeAsync(message, TestContext.Current.CancellationToken);

    private IDocumentStore Store() => fixture.Host.Services.GetRequiredService<IDocumentStore>();

    private async Task<(string TenantId, string BotPhoneId, string Phone, string SagaId)> OpenWindowAsync(string text)
    {
        var tenantId = $"tenant-window-{Guid.NewGuid():N}";
        var botPhoneId = $"bot-{Guid.NewGuid():N}";
        var phone = $"346{Random.Shared.Next(10000000, 99999999)}";
        await fixture.SeedTenantAsync(tenantId, botPhoneId);
        var sagaId = ChatSagaIds.For(tenantId, phone);
        await StoreSagaAsync(sagaId, tenantId, botPhoneId, phone, text, DateTimeOffset.UtcNow);
        return (tenantId, botPhoneId, phone, sagaId);
    }

    private async Task<long> StartedAtAsync(string sagaId)
    {
        var saga = await LoadSagaAsync(sagaId);
        saga.Should().NotBeNull();
        return saga!.StartedAt.UtcTicks;
    }

    private async Task<ChatDebounceSaga?> LoadSagaAsync(string sagaId)
    {
        using var query = Store().QuerySession();
        return await query.LoadAsync<ChatDebounceSaga>(sagaId, TestContext.Current.CancellationToken);
    }

    private async Task<ChatDebounceMarker?> LoadMarkerAsync(string sagaId)
    {
        using var query = Store().QuerySession();
        return await query.LoadAsync<ChatDebounceMarker>(sagaId, TestContext.Current.CancellationToken);
    }

    private async Task<int> ReplyCountAsync(string tenantId, string phone)
    {
        using var query = Store().LightweightSession(tenantId);
        var replies = await query.Events.FetchStreamAsync(phone, token: TestContext.Current.CancellationToken);
        return replies.Select(e => e.Data).OfType<ReplyGenerated>().Count();
    }

    private async Task StoreSagaAsync(
        string sagaId,
        string tenantId,
        string botPhoneId,
        string phone,
        string text,
        DateTimeOffset startedAt)
    {
        using var setup = Store().LightweightSession();
        setup.Store(new ChatDebounceSaga
        {
            Id = sagaId,
            TenantId = tenantId,
            BotPhoneNumberId = botPhoneId,
            PhoneNumber = phone,
            CombinedText = text,
            StartedAt = startedAt
        });
        await setup.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}