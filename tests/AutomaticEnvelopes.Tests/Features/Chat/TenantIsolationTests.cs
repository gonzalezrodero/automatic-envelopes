using System.Data;
using AutomaticEnvelopes.Api.Core.Entities;
using AutomaticEnvelopes.Api.Core.Events;
using AutomaticEnvelopes.Api.Features.Chat;
using AutomaticEnvelopes.Api.Features.Chat.Handlers;
using AutomaticEnvelopes.Api.Features.Chat.Models;
using AutomaticEnvelopes.Api.Features.Knowledge.Services;
using AutomaticEnvelopes.Api.Features.Tenancy;
using AutomaticEnvelopes.Api.Features.Tenants.Sama.CampusPricing;
using AwesomeAssertions;
using JasperFx.Events;
using Marten;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Wolverine;

namespace AutomaticEnvelopes.Tests.Features.Chat;

public class TenantIsolationTests
{
    [Fact]
    public async Task Handler_UsesThisTenantsPromptAndTools_AndSearchesOnlyThatTenant()
    {
        const string tenantId = "club-a";
        const string persona = "PERSONA_DEL_CLUB_A";
        var profile = new TenantProfile
        {
            Id = tenantId,
            BotPhoneNumberId = "bot-phone",
            Name = "Club A",
            SystemPrompt = persona,
            PrivacyPolicyUrl = "https://club-a.example/privacy",
            EnabledTools = [CampusCalculatorTool.ToolName]
        };

        var searched = new List<string>();
        string? prompt = null;
        string? calledTenant = null;
        IReadOnlyCollection<string>? enabled = null;

        var knowledge = new Mock<IKnowledgeBaseService>();
        knowledge.Setup(item => item.SearchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, int, CancellationToken>((id, _, _, _) => searched.Add(id))
            .ReturnsAsync([]);

        var chat = new Mock<IChatService>();
        chat.Setup(item => item.GetResponseAsync(
                It.IsAny<string>(),
                It.IsAny<List<ChatMessage>>(),
                It.IsAny<string>(),
                It.IsAny<IReadOnlyCollection<string>?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, List<ChatMessage>, string, IReadOnlyCollection<string>?, CancellationToken>((system, _, tenant, tools, _) =>
            {
                prompt = system;
                calledTenant = tenant;
                enabled = tools;
            })
            .ReturnsAsync("La respuesta.");

        var bus = new Mock<IMessageBus>();
        bus.Setup(item => item.InvokeAsync(It.IsAny<ReplyGenerated>(), It.IsAny<CancellationToken>(), null))
            .Returns(Task.CompletedTask);

        await ChatSessionHandler.Handle(
            new AnalyzeChatSession("34600111222", tenantId, "bot-phone", "¿Horario?"),
            SessionFor(profile),
            knowledge.Object,
            chat.Object,
            bus.Object,
            NullLogger.Instance,
            CancellationToken.None);

        searched.Should().Equal(tenantId);
        calledTenant.Should().Be(tenantId);
        enabled.Should().Equal(CampusCalculatorTool.ToolName);
        prompt.Should().NotBeNull();
        prompt.Should().Contain(persona);
        prompt.Should().NotContain("CAMPUS INSCRIPTIONS");
        prompt.Should().NotContain("PERSONA_DEL_CLUB_B");
        prompt.Should().NotContain(BotPrompts.DeleteCommand);
    }

    [Fact]
    public async Task Handler_PassesNoTools_WhenTheTenantListIsMissing()
    {
        var profile = new TenantProfile
        {
            Id = "club-b",
            BotPhoneNumberId = "bot-phone",
            SystemPrompt = "Persona B",
            EnabledTools = null
        };
        IReadOnlyCollection<string>? enabled = null;
        var chat = new Mock<IChatService>();
        chat.Setup(item => item.GetResponseAsync(
                It.IsAny<string>(),
                It.IsAny<List<ChatMessage>>(),
                "club-b",
                It.IsAny<IReadOnlyCollection<string>?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, List<ChatMessage>, string, IReadOnlyCollection<string>?, CancellationToken>((_, _, _, tools, _) => enabled = tools)
            .ReturnsAsync("Ok.");
        var knowledge = new Mock<IKnowledgeBaseService>();
        knowledge.Setup(item => item.SearchAsync("club-b", It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var bus = new Mock<IMessageBus>();
        bus.Setup(item => item.InvokeAsync(It.IsAny<ReplyGenerated>(), It.IsAny<CancellationToken>(), null))
            .Returns(Task.CompletedTask);

        await ChatSessionHandler.Handle(
            new AnalyzeChatSession("34600999888", "club-b", "bot-phone", "Hola"),
            SessionFor(profile),
            knowledge.Object,
            chat.Object,
            bus.Object,
            NullLogger.Instance,
            CancellationToken.None);

        enabled.Should().BeNull();
        knowledge.Verify(
            item => item.SearchAsync("club-a", It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static IDocumentStore SessionFor(TenantProfile profile)
    {
        var events = new Mock<Marten.Events.IEventStoreOperations>();
        events.Setup(item => item.FetchStreamAsync(
                It.IsAny<string>(),
                It.IsAny<long>(),
                It.IsAny<DateTimeOffset?>(),
                It.IsAny<long>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([new Event<ReplyGenerated>(new ReplyGenerated("old", profile.BotPhoneNumberId, "34600", "Hola", profile.Id))]);
        events.Setup(item => item.Append(It.IsAny<string>(), It.IsAny<object[]>()));

        var session = new Mock<IDocumentSession>();
        session.Setup(item => item.Events).Returns(events.Object);
        session.Setup(item => item.LoadAsync<TenantProfile>(profile.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(profile);
        session.Setup(item => item.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var store = new Mock<IDocumentStore>();
        store.Setup(item => item.LightweightSession(profile.Id, It.IsAny<IsolationLevel>())).Returns(session.Object);
        return store.Object;
    }
}
