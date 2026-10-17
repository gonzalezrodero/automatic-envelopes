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
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyVerifyToken_FailsValidation(string? verifyToken)
    {
        var result = new WhatsAppOptionsValidator().Validate(null, new WhatsAppOptions { VerifyToken = verifyToken ?? string.Empty });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Be(WhatsAppOptionsValidator.VerifyTokenRequired);
    }

    [Fact]
    public void EmptyVerifyToken_FailsWhenTheHostStarts()
    {
        using var host = BuildHost(new Dictionary<string, string?>
        {
            ["WhatsApp:VerifyToken"] = " "
        });

        var act = () => host.Start();

        act.Should().Throw<OptionsValidationException>()
            .Which.Failures.Should().Contain(WhatsAppOptionsValidator.VerifyTokenRequired);
    }

    [Fact]
    public void ConfiguredVerifyToken_AllowsTheHostToStart()
    {
        using var host = BuildHost(new Dictionary<string, string?>
        {
            ["WhatsApp:VerifyToken"] = "verify-me"
        });

        host.Start();

        host.Services.GetRequiredService<IOptions<WhatsAppOptions>>().Value.VerifyToken.Should().Be("verify-me");
    }

    private static IHost BuildHost(Dictionary<string, string?> values)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = "Testing",
            ApplicationName = "whatsapp-options-test"
        });
        builder.Configuration.AddInMemoryCollection(values);
        builder.Services.AddWhatsAppOptions(builder.Configuration);
        return builder.Build();
    }
}
