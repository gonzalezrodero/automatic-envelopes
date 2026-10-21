using AutomaticEnvelopes.Api.Features.Chat;
using AutomaticEnvelopes.Api.Features.Chat.Tools;
using AwesomeAssertions;

namespace AutomaticEnvelopes.Tests.Features.Chat.Tools;

public class TenantToolGateTests
{
    [Fact]
    public void Select_FailsClosed_WhenConfigIsMissingOrBlank()
    {
        var registered = new IBedrockTool[] { new NamedTool("price") };

        TenantToolGate.KnownNames.Should().BeEmpty();
        TenantToolGate.Select(registered, null, TenantToolGate.KnownNames).Should().BeEmpty();
        TenantToolGate.Select(registered, [], ["price"]).Should().BeEmpty();
        TenantToolGate.Select(registered, [" ", ""], ["price"]).Should().BeEmpty();
        TenantToolGate.Select(registered, ["price"], []).Should().BeEmpty();
    }

    [Fact]
    public void Select_ReturnsOnlyAllowlistedNames()
    {
        var price = new NamedTool("price");
        var other = new NamedTool("other");

        var selected = TenantToolGate.Select([price, other], [" price ", "price"], ["price"]);
        var foreign = TenantToolGate.Select([price, other], ["other"], ["price"]);

        selected.Should().ContainSingle().Which.Should().BeSameAs(price);
        foreign.Should().BeEmpty();
    }

    [Fact]
    public void GenericPrompt_HasNoClubSpecificRule()
    {
        BotPrompts.SystemPromptTemplate.Should().NotContain("CAMPUS INSCRIPTIONS");
        BotPrompts.SystemPromptTemplate.Should().NotContain("menjador");
        BotPrompts.SystemPromptTemplate.Should().NotContain("socis");
        BotPrompts.SystemPromptTemplate.Should().NotContain("Samà");
        BotPrompts.SystemPromptTemplate.Should().Contain("[[UNKNOWN]]");
        BotPrompts.SystemPromptTemplate.Should().NotContain(BotPrompts.DeleteCommand);
        BotPrompts.DeleteCommands.Should().Contain(BotPrompts.DeleteCommand);
    }

    private sealed class NamedTool(string name) : IBedrockTool
    {
        public string Name => name;

        public Amazon.BedrockRuntime.Model.ToolSpecification GetSpecification() =>
            throw new NotSupportedException();

        public Task<string> ExecuteAsync(string jsonArguments, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
