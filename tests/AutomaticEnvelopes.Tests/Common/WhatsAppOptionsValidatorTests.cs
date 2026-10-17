using System.Text.Json;
using AutomaticEnvelopes.Api.Common.Configuration;
using AutomaticEnvelopes.Api.Features.WhatsAppWebhook;
using AutomaticEnvelopes.Tests.Features.AdminAuth;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AutomaticEnvelopes.Tests.Common;

public class WhatsAppOptionsValidatorTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankVerifyToken_FailsWhenTheHostStarts(string verifyToken)
    {
        using var host = BuildHost(verifyToken, validateVerifyToken: true);

        var act = () => host.Start();

        act.Should().Throw<OptionsValidationException>()
            .Which.Failures.Should().Contain(WhatsAppOptions.VerifyTokenRequired);
    }

    [Fact]
    public void ConfiguredVerifyToken_AllowsTheHostToStart()
    {
        using var host = BuildHost("verify-me", validateVerifyToken: true);

        host.Start();

        host.Services.GetRequiredService<IOptions<WhatsAppOptions>>().Value.VerifyToken.Should().Be("verify-me");
    }

    [Fact]
    public void WorkerRegistration_StartsWhenVerifyTokenIsMissing()
    {
        using var host = BuildHost(" ", validateVerifyToken: false);

        host.Start();

        host.Services.GetRequiredService<IOptions<WhatsAppOptions>>().Value.VerifyToken.Should().Be(" ");
    }

    [Fact]
    public void ShippedAppSettings_OmitsWhatsAppSecrets()
    {
        var shipped = File.ReadAllText(AdminAuthAppSettings.Path());
        shipped.Should().NotContain("dummy_verify_token");
        shipped.Should().NotContain("dummy_secret");
        shipped.Should().NotContain("dummy_token");
        shipped.Should().NotContain("26096431083348486");

        using var document = JsonDocument.Parse(shipped);
        var whatsApp = document.RootElement.GetProperty("WhatsApp");
        whatsApp.TryGetProperty("VerifyToken", out _).Should().BeFalse();
        whatsApp.TryGetProperty("AppSecret", out _).Should().BeFalse();
        whatsApp.TryGetProperty("AccessToken", out _).Should().BeFalse();
        whatsApp.TryGetProperty("PhoneNumberId", out _).Should().BeFalse();
        whatsApp.GetProperty("BaseUrl").GetString().Should().NotBeNullOrWhiteSpace();

        var developmentPath = Path.Combine(Path.GetDirectoryName(AdminAuthAppSettings.Path())!, "appsettings.Development.json");
        var development = File.ReadAllText(developmentPath);
        development.Should().Contain("dummy_verify_token");
        development.Should().Contain("dummy_secret");
        development.Should().Contain("dummy_token");
    }

    [Fact]
    public void PayloadProcessor_StillRequiresAppSecret()
    {
        var act = () => new WhatsAppPayloadProcessor(
            Options.Create(new WhatsAppOptions()),
            NullLogger<WhatsAppPayloadProcessor>.Instance);

        act.Should().Throw<ArgumentException>();
    }

    private static IHost BuildHost(string verifyToken, bool validateVerifyToken)
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
        builder.Services.AddWhatsAppOptions(builder.Configuration, validateVerifyToken);
        return builder.Build();
    }
}
