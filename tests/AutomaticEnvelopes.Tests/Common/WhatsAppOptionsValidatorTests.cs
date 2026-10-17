using AutomaticEnvelopes.Api.Common.Configuration;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AutomaticEnvelopes.Tests.Common;

public class WhatsAppOptionsValidatorTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankVerifyToken_FailsWhenTheHostStarts(string verifyToken)
    {
        using var host = BuildHost(verifyToken);

        var act = () => host.Start();

        act.Should().Throw<OptionsValidationException>()
            .Which.Failures.Should().Contain(WhatsAppOptions.VerifyTokenRequired);
    }

    [Fact]
    public void ConfiguredVerifyToken_AllowsTheHostToStart()
    {
        using var host = BuildHost("verify-me");

        host.Start();

        host.Services.GetRequiredService<IOptions<WhatsAppOptions>>().Value.VerifyToken.Should().Be("verify-me");
    }

    private static IHost BuildHost(string verifyToken)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = "Testing",
            ApplicationName = "whatsapp-options-test"
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WhatsApp:VerifyToken"] = verifyToken
        });
        builder.Services.AddWhatsAppOptions(builder.Configuration);
        return builder.Build();
    }
}
