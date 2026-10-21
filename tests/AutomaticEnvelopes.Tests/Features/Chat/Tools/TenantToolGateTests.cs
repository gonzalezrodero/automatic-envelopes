using AutomaticEnvelopes.Api.Features.Chat;
using AutomaticEnvelopes.Api.Features.Chat.Tools;
using AutomaticEnvelopes.Api.Features.Tenants.Sama.CampusPricing;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutomaticEnvelopes.Tests.Features.Chat.Tools;

public class TenantToolGateTests
{
    [Fact]
    public void Select_FailsClosed_WhenConfigIsMissing()
    {
        var selected = TenantToolGate.Select([Calculator(), Other("foreign_tool")], null);

        selected.Should().BeEmpty();
    }

    [Fact]
    public void Select_FailsClosed_WhenConfigIsEmptyOrBlank()
    {
        var registered = new IBedrockTool[] { Calculator(), Other("foreign_tool") };

        TenantToolGate.KnownNames.Should().Equal(CampusCalculatorTool.ToolName);
        TenantToolGate.Select(registered, []).Should().BeEmpty();
        TenantToolGate.Select(registered, [" ", ""]).Should().BeEmpty();
        TenantToolGate.Select(registered, ["club-basquet-sama"]).Should().BeEmpty();
    }

    [Fact]
    public void Select_EnablesOnlyTheNamedTool()
    {
        var calculator = Calculator();
        var foreign = Other("foreign_tool");

        var forSama = TenantToolGate.Select([calculator, foreign], [CampusCalculatorTool.ToolName]);
        var forOther = TenantToolGate.Select([calculator, foreign], ["foreign_tool"]);

        forSama.Should().ContainSingle().Which.Name.Should().Be(CampusCalculatorTool.ToolName);
        forOther.Should().ContainSingle().Which.Name.Should().Be("foreign_tool");
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

    private static CampusCalculatorTool Calculator() => new(NullLogger<CampusCalculatorTool>.Instance);

    private static IBedrockTool Other(string name) => new NamedTool(name);

    private sealed class NamedTool(string name) : IBedrockTool
    {
        public string Name => name;

        public Amazon.BedrockRuntime.Model.ToolSpecification GetSpecification() =>
            throw new NotSupportedException();

        public Task<string> ExecuteAsync(string jsonArguments, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
