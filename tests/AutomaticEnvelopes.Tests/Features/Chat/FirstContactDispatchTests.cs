using System.Data;
using AutomaticEnvelopes.Api.Core.Entities;
using AutomaticEnvelopes.Api.Core.Events;
using AutomaticEnvelopes.Api.Features.Chat;
using AutomaticEnvelopes.Api.Features.Chat.Handlers;
using AutomaticEnvelopes.Api.Features.Chat.Models;
using AutomaticEnvelopes.Api.Features.Knowledge.Services;
using AutomaticEnvelopes.Api.Features.Tenancy;
using AwesomeAssertions;
using JasperFx.Events;
using Marten;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Wolverine;

namespace AutomaticEnvelopes.Tests.Features.Chat;

public class FirstContactInstructionTests
{
    [Fact]
    public void Instruction_FillsTheSpanishReference()
    {
        var notice = BotPrompts.FirstContactInstruction("  Club {Samà}  ", "  https://club.example/p  ");

        notice.Should().Contain("translated faithfully into the language the user wrote in");
        notice.Should().Contain("AI assistant on behalf of Club {Samà}");
        notice.Should().Contain("Club {Samà} is responsible for the data");
        notice.Should().Contain("privacy URL unchanged");
        notice.Should().Contain($"command {BotPrompts.DeleteCommand} exactly as written and untranslated");
        notice.Should().Contain("Soy un asistente de inteligencia artificial y escribo en nombre de Club {Samà}.");
        notice.Should().Contain("Club {Samà} es responsable de tus datos. Política de privacidad: https://club.example/p.");
        notice.Should().Contain($"escribe {BotPrompts.DeleteCommand}.");
        notice.Should().NotContain("  ");
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", " ")]
    public void Instruction_UsesFallbacksWhenTheTenantHasNoNameOrUrl(string? name, string? url)
    {
        var notice = BotPrompts.FirstContactInstruction(name, url);

        notice.Should().Contain("la organización");
        notice.Should().Contain(BotPrompts.FallbackPrivacyPolicyUrl);
    }
}

public class FirstContactDispatchTests
{
    private const string Phone = "34600111222";
    private const string PrivacyUrl = "https://club.example/privacidad";

    [Fact]
    public async Task GivenANewUser_WhenHandled_ThenThePromptCarriesTheNoticeOnceAndTheAnswerFollows()
    {
        var harness = new Harness(Tenant(), "El campus cuesta 90 euros.", []);

        await Handle(harness, "¿Cuánto cuesta el campus?");

        harness.Replies.Should().ContainSingle().Which.Text.Should().Be("El campus cuesta 90 euros.");
        harness.Prompt.Should().Contain(BotPrompts.FirstContactInstruction("Club Samà", PrivacyUrl));
        harness.Prompt.Split("FIRST CONTACT").Should().HaveCount(2);
    }

    [Fact]
    public async Task GivenAPreviousReply_WhenHandled_ThenTheNoticeIsLeftOut()
    {
        var previous = Event(new ReplyGenerated("old", "bot-phone", Phone, "Hola.", "club-sama"));
        var harness = new Harness(Tenant(), "Abre a las 9.", [previous]);

        await Handle(harness, "¿A qué hora abre?");

        harness.Replies.Should().ContainSingle().Which.Text.Should().Be("Abre a las 9.");
        harness.Prompt.Should().NotContain("FIRST CONTACT").And.NotContain(PrivacyUrl);
    }

    [Fact]
    public async Task GivenSeveralOpeningMessages_WhenHandled_ThenTheNoticeIsStillIncludedOnce()
    {
        var history = new[]
        {
            Event(new MessageReceived("m1", Phone, "Hola", "club-sama", "bot-phone", DateTimeOffset.UtcNow)),
            Event(new MessageReceived("m2", Phone, "¿Hay plaza?", "club-sama", "bot-phone", DateTimeOffset.UtcNow))
        };
        var harness = new Harness(Tenant(), "Sí, queda plaza.", history);

        await Handle(harness, "Hola\n¿Hay plaza?");

        harness.Replies.Should().ContainSingle().Which.Text.Should().Be("Sí, queda plaza.");
        harness.Prompt.Split("FIRST CONTACT").Should().HaveCount(2);
    }

    [Fact]
    public async Task GivenNoNameOrPrivacyUrl_WhenHandled_ThenTheNoticeUsesTheFallbacks()
    {
        var tenant = new TenantProfile
        {
            Id = "club-sama",
            BotPhoneNumberId = "bot-phone",
            Name = " ",
            ShortName = "Samà",
            PrivacyPolicyUrl = " "
        };
        var harness = new Harness(tenant, "De acuerdo.", []);

        await Handle(harness, "Hola");

        harness.Prompt.Should().Contain(BotPrompts.FirstContactInstruction("Samà", " "));
    }

    [Theory]
    [InlineData("BORRAR DATOS")]
    [InlineData("  esborrar dades ")]
    [InlineData("delete data")]
    public async Task GivenADeleteCommand_WhenHandled_ThenTheNoticeIsNotSent(string command)
    {
        var harness = new Harness(Tenant(), "unused", []);

        await Handle(harness, command);

        harness.Replies.Should().BeEmpty();
        harness.Prompt.Should().BeEmpty();
        harness.Chat.Verify(
            item => item.GetResponseAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static Task Handle(Harness harness, string text) =>
        ChatSessionHandler.Handle(
            new AnalyzeChatSession(Phone, harness.Tenant.Id, "bot-phone", text),
            harness.Store.Object,
            harness.Knowledge.Object,
            harness.Chat.Object,
            harness.Bus.Object,
            NullLogger.Instance,
            CancellationToken.None);

    private static TenantProfile Tenant() => new()
    {
        Id = "club-sama",
        BotPhoneNumberId = "bot-phone",
        Name = "Club Samà",
        PrivacyPolicyUrl = PrivacyUrl,
        SystemPrompt = "Eres el asistente del club."
    };

    private static IEvent Event(object data)
    {
        var evt = new Mock<IEvent>();
        evt.SetupGet(item => item.Data).Returns(data);
        return evt.Object;
    }

    private sealed class Harness
    {
        public Mock<IDocumentStore> Store { get; } = new();
        public Mock<IKnowledgeBaseService> Knowledge { get; } = new();
        public Mock<IChatService> Chat { get; } = new();
        public Mock<IMessageBus> Bus { get; } = new();
        public TenantProfile Tenant { get; }
        public List<ReplyGenerated> Replies { get; } = [];
        public string Prompt { get; private set; } = string.Empty;

        public Harness(TenantProfile tenant, string answer, IReadOnlyList<IEvent> history)
        {
            Tenant = tenant;
            var events = new Mock<Marten.Events.IEventStoreOperations>();
            events.Setup(item => item.FetchStreamAsync(
                    Phone,
                    It.IsAny<long>(),
                    It.IsAny<DateTimeOffset?>(),
                    It.IsAny<long>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(history.ToArray());
            events.Setup(item => item.Append(Phone, It.IsAny<object[]>()))
                .Callback<string, object[]>((_, appended) => Replies.AddRange(appended.OfType<ReplyGenerated>()));

            var session = new Mock<IDocumentSession>();
            session.Setup(item => item.Events).Returns(events.Object);
            session.Setup(item => item.LoadAsync<TenantProfile>(tenant.Id, It.IsAny<CancellationToken>()))
                .ReturnsAsync(tenant);
            session.Setup(item => item.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

            Store.Setup(item => item.LightweightSession(tenant.Id, It.IsAny<IsolationLevel>())).Returns(session.Object);
            Knowledge.Setup(item => item.SearchAsync(tenant.Id, It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<DocumentChunk>());
            Chat.Setup(item => item.GetResponseAsync(
                    It.IsAny<string>(),
                    It.IsAny<List<ChatMessage>>(),
                    tenant.Id,
                    It.IsAny<CancellationToken>()))
                .Callback<string, List<ChatMessage>, string, CancellationToken>((prompt, _, _, _) => Prompt = prompt)
                .ReturnsAsync(answer);
            Bus.Setup(item => item.InvokeAsync(It.IsAny<ReplyGenerated>(), It.IsAny<CancellationToken>(), null))
                .Returns(Task.CompletedTask);
            Bus.Setup(item => item.InvokeAsync(It.IsAny<DeleteChatHistoryCommand>(), It.IsAny<CancellationToken>(), null))
                .Returns(Task.CompletedTask);
        }
    }
}
