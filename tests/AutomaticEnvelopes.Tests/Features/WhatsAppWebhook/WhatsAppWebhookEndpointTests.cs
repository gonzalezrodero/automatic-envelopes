using AutomaticEnvelopes.Api.Common.Configuration;
using AutomaticEnvelopes.Api.Features.WhatsAppWebhook;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AutomaticEnvelopes.Tests.Features.WhatsAppWebhook;

public class WhatsAppWebhookEndpointTests
{
    [Theory]
    [InlineData("secret-token", "secret-token", 200, "1158201444")]
    [InlineData("wrong-token", "secret-token", 403, "")]
    [InlineData("", "", 403, "")]
    [InlineData("   ", "   ", 403, "")]
    public void VerifyChallenge_MatchesTheConfiguredToken(string provided, string configured, int status, string body)
    {
        var result = new WhatsAppWebhookEndpoint().VerifyChallenge(
            "subscribe",
            provided,
            "1158201444",
            Options.Create(new WhatsAppOptions { VerifyToken = configured }),
            NullLogger<WhatsAppWebhookEndpoint>.Instance);

        var statusCode = result switch
        {
            ContentHttpResult content => content.StatusCode ?? StatusCodes.Status200OK,
            IStatusCodeHttpResult coded => coded.StatusCode,
            _ => null
        };
        statusCode.Should().Be(status);
        if (result is ContentHttpResult ok)
        {
            ok.ResponseContent.Should().Be(body);
        }
    }
}
